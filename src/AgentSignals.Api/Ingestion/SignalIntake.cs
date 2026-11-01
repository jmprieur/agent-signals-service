using System.Text;
using System.Text.Json;
using AgentSignals.Api.Protocol;
using AgentSignals.Api.Storage;

namespace AgentSignals.Api.Ingestion;

/// <summary>What the service did with one submitted signal.</summary>
public sealed record SubmissionResult(
    string Status,
    string? SignalId,
    bool? Conforming,
    string? SourceFormat,
    IReadOnlyList<string> Issues,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<PrivacyFinding> PrivacyFindings,
    string? Message)
{
    public const string Accepted = "accepted";
    public const string Duplicate = "duplicate";
    public const string Rejected = "rejected";

    public static SubmissionResult Reject(string message, IReadOnlyList<PrivacyFinding>? findings = null) =>
        new(Rejected, null, null, null, [], [], findings ?? [], message);
}

/// <summary>
/// Shared intake for HTTP and MCP: privacy gate, archive, then queue for Kusto. Non-conforming
/// signals are kept (the protocol asks to capture what happened) but never scored.
/// </summary>
public sealed class SignalIntake(ISignalArchive archive, IngestionQueue queue, TimeProvider time, ILogger<SignalIntake> logger)
{
    public const int MaxSignalBytes = 64 * 1024;

    public async Task<SubmissionResult> SubmitAsync(JsonElement signal, string submitter, CancellationToken cancellationToken)
    {
        if (signal.ValueKind != JsonValueKind.Object)
        {
            return SubmissionResult.Reject("A signal must be a JSON object.");
        }

        if (Encoding.UTF8.GetByteCount(signal.GetRawText()) > MaxSignalBytes)
        {
            return SubmissionResult.Reject($"A signal must be at most {MaxSignalBytes / 1024} KB.");
        }

        var blocking = PrivacyScanner.Scan(signal).Where(f => f.Blocking).ToArray();
        if (blocking.Length > 0)
        {
            return SubmissionResult.Reject(
                "The signal appears to contain private data and was not stored. Remove it and send the signal again.", blocking);
        }

        var record = SignalRecord.FromArchived(SignalRecord.PrepareForArchive(signal), time.GetUtcNow(), submitter);
        var stored = await archive.StoreAsync(record, cancellationToken);
        if (stored == ArchiveResult.Stored && !queue.TryEnqueue(record))
        {
            logger.LogWarning("Ingestion queue is full; signal {SignalId} will be loaded by the reconciler.", record.SignalId);
        }

        return new SubmissionResult(
            stored == ArchiveResult.Stored ? SubmissionResult.Accepted : SubmissionResult.Duplicate,
            record.SignalId,
            record.IsConforming,
            record.SourceFormat,
            record.Issues,
            record.Warnings,
            [],
            record.IsConforming ? null : "Stored, but not used in scores until it conforms to the protocol.");
    }
}
