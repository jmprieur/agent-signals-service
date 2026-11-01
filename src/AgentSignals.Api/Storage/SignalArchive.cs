using System.Globalization;
using System.Runtime.CompilerServices;
using AgentSignals.Api.Protocol;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace AgentSignals.Api.Storage;

public enum ArchiveResult
{
    Stored,
    AlreadyStored,
}

/// <summary>A signal as it sits in the archive, before translation.</summary>
public sealed record ArchivedSignal(string Json, DateTimeOffset ReceivedAt, string Submitter);

/// <summary>The durable record of every accepted signal. Kusto can always be rebuilt from it.</summary>
public interface ISignalArchive
{
    Task<ArchiveResult> StoreAsync(SignalRecord record, CancellationToken cancellationToken);

    IAsyncEnumerable<ArchivedSignal> ReadReceivedSinceAsync(DateTimeOffset since, CancellationToken cancellationToken);
}

/// <summary>One immutable blob per signal, named by content hash under a receipt-date folder.</summary>
public sealed class BlobSignalArchive(BlobContainerClient container) : ISignalArchive
{
    private const string ReceivedAtKey = "received_at";
    private const string SubmitterKey = "submitter";

    public async Task<ArchiveResult> StoreAsync(SignalRecord record, CancellationToken cancellationToken)
    {
        var blob = container.GetBlobClient(record.BlobName);
        try
        {
            await blob.UploadAsync(BinaryData.FromString(record.ArchivedJson), new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
                Metadata = new Dictionary<string, string>
                {
                    [ReceivedAtKey] = record.ReceivedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                    [SubmitterKey] = MetadataSafe(record.Submitter),
                },
            }, cancellationToken);
            return ArchiveResult.Stored;
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            return ArchiveResult.AlreadyStored;
        }
    }

    public async IAsyncEnumerable<ArchivedSignal> ReadReceivedSinceAsync(
        DateTimeOffset since, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var day = since.UtcDateTime.Date; day <= DateTime.UtcNow.Date; day = day.AddDays(1))
        {
            var prefix = $"{day:yyyy}/{day:MM}/{day:dd}/";
            await foreach (var item in container.GetBlobsAsync(
                new GetBlobsOptions { Traits = BlobTraits.Metadata, Prefix = prefix }, cancellationToken))
            {
                var receivedAt = item.Metadata.TryGetValue(ReceivedAtKey, out var text) &&
                                 DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                    ? parsed
                    : item.Properties.CreatedOn ?? DateTimeOffset.UtcNow;
                if (receivedAt < since)
                {
                    continue;
                }

                var content = await container.GetBlobClient(item.Name).DownloadContentAsync(cancellationToken);
                yield return new ArchivedSignal(
                    content.Value.Content.ToString(),
                    receivedAt,
                    item.Metadata.TryGetValue(SubmitterKey, out var submitter) ? submitter : "unknown");
            }
        }
    }

    private static string MetadataSafe(string value) =>
        new(value.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.').Take(128).ToArray());
}
