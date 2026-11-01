using System.Threading.Channels;
using AgentSignals.Api.Kusto;
using AgentSignals.Api.Protocol;

namespace AgentSignals.Api.Ingestion;

/// <summary>Signals waiting to be loaded into Kusto. Losing one is safe: the reconciler replays it from the archive.</summary>
public sealed class IngestionQueue
{
    private readonly Channel<SignalRecord> _channel = Channel.CreateBounded<SignalRecord>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    public bool TryEnqueue(SignalRecord record) => _channel.Writer.TryWrite(record);

    public ChannelReader<SignalRecord> Reader => _channel.Reader;
}

/// <summary>Loads queued signals into Kusto in small batches, one command per table.</summary>
public sealed class KustoIngestionWorker(
    IngestionQueue queue,
    IKustoGateway kusto,
    KustoSchemaInitializer schema,
    ILogger<KustoIngestionWorker> logger) : BackgroundService
{
    private const int MaxBatchSignals = 100;
    private const int MaxBatchBytes = 256 * 1024;
    private const int MaxAttempts = 4;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await schema.Ready.WaitAsync(stoppingToken);

        while (await queue.Reader.WaitToReadAsync(stoppingToken))
        {
            // Give a burst a moment to accumulate so it lands as one command per table.
            await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);

            var batch = new List<SignalRecord>();
            var bytes = 0;
            while (batch.Count < MaxBatchSignals && bytes < MaxBatchBytes && queue.Reader.TryRead(out var record))
            {
                batch.Add(record);
                bytes += record.ArchivedJson.Length;
            }

            await LoadAsync(batch, stoppingToken);
        }
    }

    public async Task LoadAsync(IReadOnlyList<SignalRecord> batch, CancellationToken cancellationToken)
    {
        var byTable = batch
            .SelectMany(SignalRows.For)
            .GroupBy(x => x.Table, x => x.Row);

        foreach (var group in byTable)
        {
            var command = group.Key.AppendCommand(group.ToList());
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await kusto.ExecuteCommandAsync(command, cancellationToken);
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && attempt < MaxAttempts)
                {
                    logger.LogWarning(ex, "Loading {Count} rows into {Table} failed (attempt {Attempt}).", group.Count(), group.Key.Name, attempt);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The signals are safe in the archive; the reconciler will load them later.
                    logger.LogError(ex, "Giving up loading {Count} rows into {Table}; the reconciler will retry.", group.Count(), group.Key.Name);
                    break;
                }
            }
        }
    }
}
