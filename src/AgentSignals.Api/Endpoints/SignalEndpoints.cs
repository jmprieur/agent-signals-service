using System.Security.Claims;
using System.Text.Json;
using AgentSignals.Api.Auth;
using AgentSignals.Api.Ingestion;
using AgentSignals.Api.Insights;
using AgentSignals.Api.Protocol;
using Azure;

namespace AgentSignals.Api.Endpoints;

public sealed record BatchSubmissionResponse(IReadOnlyList<SubmissionResult> Results);

public static class SignalEndpoints
{
    public const int MaxBatch = 100;

    public static WebApplication MapSignalEndpoints(this WebApplication app)
    {
        app.MapGet("/", () => Results.Ok(new
        {
            Service = "Agent Signals",
            Protocol = $"Agent Signals {SignalProtocol.CurrentSchemaVersion} (https://github.com/jennyf19/agentic-devops/tree/main/agent-signals)",
            AcceptedFormats = new[] { SignalProtocol.ProtocolFormat, SignalProtocol.AlasFormat },
            Submit = "POST /v1/signals (one signal object, or an array of up to 100)",
            Reports = SignalInsights.Reports.Select(r => new { Path = $"GET /v1/insights/{r.Name}?days=7&top=50&skill=", r.Description }),
            Mcp = "/mcp",
        })).AllowAnonymous();

        app.MapPost("/v1/signals", SubmitAsync)
            .RequireAuthorization(SignalPolicies.Write)
            .RequireRateLimiting("signals")
            .WithName("SubmitSignals")
            .WithDescription("Submit one Agent Signal or an array of them. Accepts protocol 0.1.x and the ALAS 1.0 dialect.");

        app.MapGet("/v1/insights/{report}", InsightAsync)
            .RequireAuthorization(SignalPolicies.Read)
            .RequireRateLimiting("signals")
            .WithName("GetInsight");

        return app;
    }

    private static async Task<IResult> SubmitAsync(HttpRequest request, SignalIntake intake, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The request body is not valid JSON.");
        }

        using (document)
        {
            var submitter = SignalAuthentication.Submitter(user);
            var root = document.RootElement;
            try
            {
                if (root.ValueKind == JsonValueKind.Array)
                {
                    var count = root.GetArrayLength();
                    if (count is 0 or > MaxBatch)
                    {
                        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: $"Send between 1 and {MaxBatch} signals per request.");
                    }

                    var results = new List<SubmissionResult>(count);
                    foreach (var signal in root.EnumerateArray())
                    {
                        results.Add(await intake.SubmitAsync(signal, submitter, cancellationToken));
                    }

                    return Results.Ok(new BatchSubmissionResponse(results));
                }

                var result = await intake.SubmitAsync(root, submitter, cancellationToken);
                return result.Status == SubmissionResult.Rejected
                    ? Results.UnprocessableEntity(result)
                    : Results.Accepted(value: result);
            }
            catch (RequestFailedException)
            {
                // Storage is unavailable. Agents should keep the signal locally (.signals/) and retry later.
                request.HttpContext.Response.Headers.RetryAfter = "60";
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Signal storage is unavailable. Keep the signal locally and retry later.");
            }
        }
    }

    private static async Task<IResult> InsightAsync(
        string report, SignalInsights insights, CancellationToken cancellationToken,
        int days = 7, int top = 50, string? skill = null)
    {
        var definition = SignalInsights.Find(report);
        if (definition is null)
        {
            return Results.NotFound(new { Message = $"Unknown report. Known reports: {string.Join(", ", SignalInsights.Reports.Select(r => r.Name))}." });
        }

        var rows = await insights.RunAsync(definition, days, top, skill, cancellationToken);
        return Results.Ok(new { Report = definition.Name, Days = Math.Clamp(days, 1, SignalInsights.MaxDays), Rows = rows });
    }
}
