using AgentSignals.Api.Kusto;
using AgentSignals.Api.Protocol;
using AgentSignals.Api.Storage;

namespace AgentSignals.Api.Ingestion;

/// <summary>
/// Finds archived signals from the last two days that never reached Kusto (a crash, a full queue,
/// a Kusto outage) and queues them again. Runs shortly after startup, then hourly.
/// </summary>
public sealed class ArchiveReconciler(
    ISignalArchive archive,
    IKustoGateway kusto,
    IngestionQueue queue,
    KustoSchemaInitializer schema,
    ILogger<ArchiveReconciler> logger) : BackgroundService
{
    private static readonly TimeSpan Lookback = TimeSpan.FromDays(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await schema.Ready.WaitAsync(stoppingToken);
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await ReconcileAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Archive reconciliation failed; it will run again next hour.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<int> ReconcileAsync(CancellationToken cancellationToken)
    {
        var since = DateTimeOffset.UtcNow - Lookback;
        var known = (await kusto.QueryAsync(
                "declare query_parameters(since: string); SignalsRaw | where received_at >= todatetime(since) | distinct signal_id",
                new Dictionary<string, string> { ["since"] = since.UtcDateTime.ToString("O") },
                cancellationToken))
            .Select(row => row["signal_id"] as string)
            .ToHashSet(StringComparer.Ordinal);

        var requeued = 0;
        await foreach (var archived in archive.ReadReceivedSinceAsync(since, cancellationToken))
        {
            var record = SignalRecord.FromArchived(archived.Json, archived.ReceivedAt, archived.Submitter);
            if (!known.Contains(record.SignalId) && queue.TryEnqueue(record))
            {
                requeued++;
            }
        }

        if (requeued > 0)
        {
            logger.LogInformation("Re-queued {Count} archived signals that were missing from Kusto.", requeued);
        }

        return requeued;
    }
}
