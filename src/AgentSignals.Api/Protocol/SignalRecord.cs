using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentSignals.Api.Protocol;

/// <summary>
/// A received signal after translation, validation and typing. Built from the archived JSON, so a
/// replay from the archive produces exactly what live intake produced.
/// </summary>
public sealed record SignalRecord(
    string SignalId,
    DateTimeOffset ReceivedAt,
    string Submitter,
    string ArchivedJson,
    string SourceFormat,
    string? SignalType,
    JsonObject Payload,
    IReadOnlyList<string> Issues,
    IReadOnlyList<string> Warnings,
    Signal? Typed,
    IReadOnlyList<HumanFeedbackRecord> HumanFeedback)
{
    /// <summary>Conforming signals feed the typed tables and every score.</summary>
    public bool IsConforming => Typed is not null && Issues.Count == 0;

    /// <summary>Archive path. Date-partitioned so recent signals can be listed cheaply for replay.</summary>
    public string BlobName => BlobNameFor(ReceivedAt, SignalId);

    public static string BlobNameFor(DateTimeOffset receivedAt, string signalId) =>
        $"{receivedAt:yyyy}/{receivedAt:MM}/{receivedAt:dd}/{signalId}.json";

    /// <summary>
    /// Prepares a submitted signal for the archive: removes fields that must never be stored.
    /// Callers must already have rejected signals with blocking privacy findings.
    /// </summary>
    public static string PrepareForArchive(JsonElement submitted)
    {
        var signal = JsonNode.Parse(submitted.GetRawText())!.AsObject();
        if (AlasTranslator.IsAlas(signal))
        {
            AlasTranslator.RemoveForbiddenFields(signal);
        }

        return signal.ToJsonString();
    }

    public static string ComputeId(string archivedJson) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(archivedJson)));

    public static SignalRecord FromArchived(string archivedJson, DateTimeOffset receivedAt, string submitter)
    {
        var archived = JsonNode.Parse(archivedJson)!.AsObject();

        var sourceFormat = SignalProtocol.ProtocolFormat;
        JsonObject payload = archived;
        HumanFeedbackRecord? standaloneFeedback = null;
        if (AlasTranslator.IsAlas(archived))
        {
            var translation = AlasTranslator.Translate(archived, receivedAt);
            sourceFormat = SignalProtocol.AlasFormat;
            payload = translation.Signal;
            standaloneFeedback = translation.StandaloneHumanFeedback;
        }

        var element = JsonSerializer.SerializeToElement(payload);
        var assessment = SignalValidator.Assess(element);
        var issues = assessment.Issues.ToList();
        var warnings = PrivacyScanner.Scan(element)
            .Where(f => !f.Blocking)
            .Select(f => $"{f.Path} contains a {f.Kind}; SIGNAL.md asks for anonymized identifiers instead.")
            .ToArray();

        Signal? typed = null;
        if (issues.Count == 0 && assessment.SignalType is not null)
        {
            try
            {
                typed = SignalJson.ReadTyped(element, assessment.SignalType);
            }
            catch (JsonException)
            {
                issues.Add("The signal matches the field contract but could not be read as a typed signal.");
            }
        }

        var feedback = new List<HumanFeedbackRecord>();
        if (standaloneFeedback is not null)
        {
            feedback.Add(standaloneFeedback);
        }

        if (typed is ExecutionSignal { HumanFeedback: { } hf } execution && (hf.Rating is not null || hf.Asked == true))
        {
            feedback.Add(new HumanFeedbackRecord(
                execution.RunId, execution.SessionId, execution.AgentName, execution.SkillUsed, execution.Timestamp,
                hf.Scope, hf.Rating, hf.Asked, hf.QuestionId, hf.Variant));
        }

        return new SignalRecord(
            ComputeId(archivedJson), receivedAt, submitter, archivedJson, sourceFormat,
            assessment.SignalType ?? AlasTranslator.StringOf(payload["signal_type"]),
            payload, issues, warnings, typed, feedback);
    }
}
