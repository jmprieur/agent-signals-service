using System.ComponentModel;
using System.Text.Json;
using AgentSignals.Api.Auth;
using AgentSignals.Api.Ingestion;
using AgentSignals.Api.Insights;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AgentSignals.Api.Mcp;

/// <summary>
/// MCP tools over the same intake and reports as the HTTP API. Results are bounded so a working
/// agent never pulls the whole signal backlog into its context.
/// </summary>
[McpServerToolType]
public sealed class SignalTools(SignalIntake intake, SignalInsights insights, IHttpContextAccessor accessor)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [McpServerTool(Name = "submit_signal")]
    [Description("Submit one Agent Signal (execution, outcome, escalation or partnership) after finishing a task. " +
                 "Follow SIGNAL.md 0.1.1. Never include code, people's names, emails, secrets or repository URLs.")]
    public async Task<string> SubmitSignal(
        [Description("The signal as a JSON object.")] JsonElement signal,
        CancellationToken cancellationToken)
    {
        var user = RequireRole(SignalRoles.Write);
        var result = await intake.SubmitAsync(signal, SignalAuthentication.Submitter(user), cancellationToken);
        return JsonSerializer.Serialize(result, Json);
    }

    [McpServerTool(Name = "get_recommendations", ReadOnly = true)]
    [Description("Ranked recommendations for a skill from partnership reviews. Outcome-validated items come first; " +
                 "treat self_report_only items as unverified. Use this, not raw signals, as prior context before a task.")]
    public Task<string> GetRecommendations(
        [Description("Skill name to filter on; empty for all skills.")] string? skill = null,
        [Description("Look-back window in days (1-90).")] int days = 14,
        [Description("Maximum rows (1-50).")] int limit = 10,
        CancellationToken cancellationToken = default) =>
        RunAsync("recommendations", days, Math.Min(limit, 50), skill, cancellationToken);

    [McpServerTool(Name = "get_skill_health", ReadOnly = true)]
    [Description("Per-skill health: low confidence, escalation rate, confidence decline and overconfidence findings.")]
    public Task<string> GetSkillHealth(
        [Description("Skill name to filter on; empty for all skills.")] string? skill = null,
        [Description("Look-back window in days (1-90).")] int days = 7,
        CancellationToken cancellationToken = default) =>
        RunAsync("skill-health", days, 50, skill, cancellationToken);

    [McpServerTool(Name = "get_calibration", ReadOnly = true)]
    [Description("Trust equation per agent and skill: how well self-reported confidence matches independent quality ratings.")]
    public Task<string> GetCalibration(
        [Description("Skill name to filter on; empty for all skills.")] string? skill = null,
        [Description("Look-back window in days (1-90).")] int days = 30,
        CancellationToken cancellationToken = default) =>
        RunAsync("calibration", days, 50, skill, cancellationToken);

    [McpServerTool(Name = "get_continuation_gap", ReadOnly = true)]
    [Description("Handoff readiness claimed by predecessors versus recovery rated by successors.")]
    public Task<string> GetContinuationGap(
        [Description("Skill name to filter on; empty for all skills.")] string? skill = null,
        [Description("Look-back window in days (1-90).")] int days = 30,
        CancellationToken cancellationToken = default) =>
        RunAsync("continuation", days, 50, skill, cancellationToken);

    [McpServerTool(Name = "get_pattern_candidates", ReadOnly = true)]
    [Description("Recurring pattern values checked against the protocol thresholds. Exact-match grouping only: " +
                 "a synthesizer should also cluster similar wording with get_pattern_evidence.")]
    public Task<string> GetPatternCandidates(
        [Description("Look-back window in days (1-90).")] int days = 7,
        [Description("Maximum rows (1-100).")] int limit = 50,
        CancellationToken cancellationToken = default) =>
        RunAsync("patterns", days, Math.Min(limit, 100), null, cancellationToken);

    [McpServerTool(Name = "get_pattern_evidence", ReadOnly = true)]
    [Description("Raw pattern values (one per run, dimension and skill) for an out-of-band synthesizer agent. " +
                 "Working agents should use get_recommendations instead.")]
    public Task<string> GetPatternEvidence(
        [Description("Skill name to filter on; empty for all skills.")] string? skill = null,
        [Description("Look-back window in days (1-90).")] int days = 7,
        [Description("Maximum rows (1-200).")] int limit = 200,
        CancellationToken cancellationToken = default) =>
        RunAsync("pattern-evidence", days, limit, skill, cancellationToken);

    [McpServerTool(Name = "get_escalations", ReadOnly = true)]
    [Description("Recent escalations where an agent asked a human for help.")]
    public Task<string> GetEscalations(
        [Description("Skill name to filter on; empty for all skills.")] string? skill = null,
        [Description("Look-back window in days (1-90).")] int days = 7,
        [Description("Maximum rows (1-50).")] int limit = 20,
        CancellationToken cancellationToken = default) =>
        RunAsync("escalations", days, Math.Min(limit, 50), skill, cancellationToken);

    [McpServerTool(Name = "get_overview", ReadOnly = true)]
    [Description("Signal counts for the window, including executions that no outcome has verified yet.")]
    public Task<string> GetOverview(
        [Description("Look-back window in days (1-90).")] int days = 7,
        CancellationToken cancellationToken = default) =>
        RunAsync("overview", days, 1, null, cancellationToken);

    private async Task<string> RunAsync(string reportName, int days, int top, string? skill, CancellationToken cancellationToken)
    {
        RequireRole(SignalRoles.Read);
        var report = SignalInsights.Find(reportName)!;
        var rows = await insights.RunAsync(report, days, top, skill, cancellationToken);
        return JsonSerializer.Serialize(new { report = report.Name, rows }, Json);
    }

    private System.Security.Claims.ClaimsPrincipal RequireRole(string role)
    {
        var user = accessor.HttpContext?.User;
        if (user is null || !user.IsInRole(role))
        {
            throw new McpException($"This tool requires the {role} app role.");
        }

        return user;
    }
}
