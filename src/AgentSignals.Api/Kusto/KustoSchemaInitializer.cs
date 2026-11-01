using System.Reflection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AgentSignals.Api.Kusto;

/// <summary>
/// Creates the tables and functions at startup (idempotent), then reports readiness.
/// The same code path runs against the local emulator and Azure Data Explorer.
/// </summary>
public sealed class KustoSchemaInitializer(IKustoGateway kusto, ILogger<KustoSchemaInitializer> logger)
    : BackgroundService, IHealthCheck
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Ready => _ready.Task;

    public static IReadOnlyList<string> Commands()
    {
        var commands = SignalTables.All.Select(t => t.CreateMergeCommand).ToList();
        commands.AddRange(FunctionCommands());
        return commands;
    }

    public static IEnumerable<string> FunctionCommands()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AgentSignals.Api.Kusto.schema.kql")
            ?? throw new InvalidOperationException("Embedded Kusto schema not found.");
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();

        foreach (var chunk in text.Split("\n//--", StringSplitOptions.None))
        {
            var command = string.Join('\n', chunk.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))).Trim();
            if (command.Length > 0)
            {
                yield return command;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var command in Commands())
                {
                    await kusto.ExecuteCommandAsync(command, stoppingToken);
                }

                logger.LogInformation("Kusto schema is up to date.");
                _ready.TrySetResult();
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Kusto schema update failed; retrying in {Delay}.", delay);
                await Task.Delay(delay, stoppingToken);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
            }
        }
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(_ready.Task.IsCompletedSuccessfully
            ? HealthCheckResult.Healthy("Kusto schema is ready.")
            : HealthCheckResult.Unhealthy("Kusto schema is not ready yet."));
}
