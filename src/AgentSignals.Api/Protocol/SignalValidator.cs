using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentSignals.Api.Protocol;

/// <summary>
/// Checks a signal against the normative field contracts in SIGNAL.md.
/// Issue messages name JSON paths but never echo submitted values.
/// </summary>
public static partial class SignalValidator
{
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:\d{2})$")]
    private static partial Regex Iso8601();

    public static SignalAssessment Assess(JsonElement signal)
    {
        if (signal.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("A signal must be a JSON object.", nameof(signal));
        }

        var issues = new List<string>();

        var signalType = RequiredString(signal, "signal_type", "$", issues);
        if (signalType is not null && !SignalProtocol.SignalTypes.Contains(signalType))
        {
            issues.Add("$.signal_type must be one of: execution, outcome, escalation, partnership.");
        }

        var schemaVersion = RequiredString(signal, "schema_version", "$", issues);
        if (schemaVersion is not null && !SignalProtocol.KnownSchemaVersions.Contains(schemaVersion))
        {
            issues.Add($"$.schema_version is not a known version (known: {string.Join(", ", SignalProtocol.KnownSchemaVersions.Order())}).");
        }

        var runId = RequiredString(signal, "run_id", "$", issues);
        if (runId is not null && !Guid.TryParseExact(runId, "D", out _))
        {
            issues.Add("$.run_id must be a UUID.");
        }

        var timestamp = ReadTimestamp(signal, issues);
        var agentName = RequiredString(signal, "agent_name", "$", issues);
        CheckCommonExtensions(signal, issues);

        var skill = OptionalString(signal, "skill_used", "$", issues) ?? OptionalString(signal, "observed_skill", "$", issues);
        string? resumedFrom = null;

        switch (signalType)
        {
            case "execution":
                resumedFrom = CheckExecution(signal, issues);
                break;
            case "outcome":
                CheckOutcome(signal, issues);
                break;
            case "escalation":
                CheckEscalation(signal, issues);
                break;
            case "partnership":
                CheckPartnership(signal, issues);
                break;
        }

        if (schemaVersion == "0.1.0" && UsesContinuationFields(signal))
        {
            issues.Add("Continuation fields require schema_version 0.1.1.");
        }

        return new SignalAssessment(signalType, schemaVersion, runId, resumedFrom, timestamp, agentName, skill, issues);
    }

    private static string? CheckExecution(JsonElement signal, List<string> issues)
    {
        if (TryGetObject(signal, "self_assessment", "$", issues, required: true, out var assessment))
        {
            Score(assessment, "confidence", "$.self_assessment", issues, required: true);
            Score(assessment, "accuracy", "$.self_assessment", issues, required: true);
            Score(assessment, "completeness", "$.self_assessment", issues, required: true);
            Score(assessment, "continuation_readiness", "$.self_assessment", issues, required: false);
        }

        Enumerated(signal, "mode", SignalProtocol.Modes, "$", issues, required: false);

        var resumedFrom = OptionalString(signal, "resumed_from_run_id", "$", issues);
        if (resumedFrom is not null && !Guid.TryParseExact(resumedFrom, "D", out _))
        {
            issues.Add("$.resumed_from_run_id must be a UUID.");
        }

        if (TryGetObject(signal, "patterns", "$", issues, required: false, out var patterns))
        {
            foreach (var property in patterns.EnumerateObject())
            {
                if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                {
                    issues.Add($"$.patterns.{SafeName(property.Name)} must be a string.");
                }
            }
        }

        StringArray(signal, "invoked_skills", "$", issues);

        if (TryGetObject(signal, "attribution", "$", issues, required: false, out var attribution))
        {
            foreach (var name in new[] { "configured_model", "observed_model", "configured_reasoning_effort", "observed_reasoning_effort" })
            {
                NullableString(attribution, name, "$.attribution", issues);
            }
        }

        if (TryGetObject(signal, "result", "$", issues, required: false, out var result))
        {
            NullableBoolean(result, "changes_applied", "$.result", issues);
            NullableBoolean(result, "pr_created", "$.result", issues);
            NonNegativeInteger(result, "turns_taken", "$.result", issues, allowNull: true);
        }

        if (TryGetObject(signal, "human_feedback", "$", issues, required: false, out var feedback))
        {
            if (feedback.TryGetProperty("rating", out var rating) && rating.ValueKind != JsonValueKind.Null)
            {
                Score(feedback, "rating", "$.human_feedback", issues, required: false);
            }

            NullableBoolean(feedback, "asked", "$.human_feedback", issues);
            NullableString(feedback, "scope", "$.human_feedback", issues);
            NullableString(feedback, "question_id", "$.human_feedback", issues);
            NullableString(feedback, "variant", "$.human_feedback", issues);
        }

        return resumedFrom;
    }

    private static void CheckOutcome(JsonElement signal, List<string> issues)
    {
        Score(signal, "quality_rating", "$", issues, required: true);
        Enumerated(signal, "effort_to_merge", SignalProtocol.EffortBuckets, "$", issues, required: false);

        if (signal.TryGetProperty("issues_found", out var found) &&
            (found.ValueKind != JsonValueKind.Array || found.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String)))
        {
            issues.Add("$.issues_found must be an array of strings.");
        }

        Enumerated(signal, "evidence_level", SignalProtocol.EvidenceLevels, "$", issues, required: false);
        NonNegativeInteger(signal, "recommendations_count", "$", issues, allowNull: false);

        if (TryGetObject(signal, "continuation", "$", issues, required: false, out var continuation))
        {
            Score(continuation, "recovery_rating", "$.continuation", issues, required: true);
            Enumerated(continuation, "effort_to_resume", SignalProtocol.EffortBuckets, "$.continuation", issues, required: false);
            Enumerated(continuation, "provenance_loss", SignalProtocol.LossBuckets, "$.continuation", issues, required: false);
            Enumerated(continuation, "authority_loss", SignalProtocol.LossBuckets, "$.continuation", issues, required: false);
            if (continuation.TryGetProperty("stale_path_revived", out var revived) &&
                revived.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                issues.Add("$.continuation.stale_path_revived must be a boolean (omit it when not assessed).");
            }
        }
    }

    private static void CheckEscalation(JsonElement signal, List<string> issues)
    {
        var hasNested = signal.TryGetProperty("escalation", out var nested) && nested.ValueKind == JsonValueKind.Object;

        if (signal.TryGetProperty("reason", out _))
        {
            RequiredString(signal, "reason", "$", issues);
        }
        else
        {
            issues.Add(hasNested && nested.TryGetProperty("reason", out _)
                ? "$.reason is required at the top level (found $.escalation.reason)."
                : "$.reason is required.");
        }

        if (signal.TryGetProperty("severity", out _))
        {
            Enumerated(signal, "severity", SignalProtocol.EscalationSeverities, "$", issues, required: true);
        }
        else
        {
            issues.Add(hasNested && nested.TryGetProperty("severity", out _)
                ? "$.severity is required at the top level (found $.escalation.severity)."
                : "$.severity is required (low, medium, high, or critical).");
        }

        if (signal.TryGetProperty("blocking", out var blocking) && blocking.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            issues.Add("$.blocking must be a boolean.");
        }

        OptionalString(signal, "requested_action", "$", issues);
    }

    private static void CheckPartnership(JsonElement signal, List<string> issues)
    {
        RequiredString(signal, "observed_agent", "$", issues);

        if (TryGetObject(signal, "observation", "$", issues, required: true, out var observation))
        {
            if (!observation.TryGetProperty("signal_count_reviewed", out var count))
            {
                issues.Add("$.observation.signal_count_reviewed is required.");
            }
            else if (count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out var n) || n < 0)
            {
                issues.Add("$.observation.signal_count_reviewed must be a non-negative integer.");
            }

            RequiredString(observation, "time_window", "$.observation", issues);
        }

        if (!signal.TryGetProperty("recommendations", out var recommendations))
        {
            return;
        }

        if (recommendations.ValueKind != JsonValueKind.Array)
        {
            issues.Add("$.recommendations must be an array.");
            return;
        }

        var index = 0;
        foreach (var recommendation in recommendations.EnumerateArray())
        {
            var path = $"$.recommendations[{index++}]";
            if (recommendation.ValueKind != JsonValueKind.Object)
            {
                issues.Add($"{path} must be an object.");
                continue;
            }

            Enumerated(recommendation, "validation_status", SignalProtocol.ValidationStatuses, path, issues, required: false);
        }
    }

    private static void CheckCommonExtensions(JsonElement signal, List<string> issues)
    {
        var sessionId = OptionalString(signal, "session_id", "$", issues);
        if (sessionId is not null && !Guid.TryParseExact(sessionId, "D", out _))
        {
            issues.Add("$.session_id must be a UUID.");
        }

        if (TryGetObject(signal, "labels", "$", issues, required: false, out var labels) &&
            labels.EnumerateObject().Any(p => p.Value.ValueKind != JsonValueKind.String))
        {
            issues.Add("$.labels values must be strings.");
        }
    }

    private static void StringArray(JsonElement obj, string name, string parent, List<string> issues)
    {
        if (obj.TryGetProperty(name, out var value) &&
            (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String)))
        {
            issues.Add($"{parent}.{name} must be an array of strings.");
        }
    }

    private static void NullableString(JsonElement obj, string name, string parent, List<string> issues)
    {
        if (obj.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
        {
            issues.Add($"{parent}.{name} must be a string.");
        }
    }

    private static void NullableBoolean(JsonElement obj, string name, string parent, List<string> issues)
    {
        if (obj.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
        {
            issues.Add($"{parent}.{name} must be a boolean.");
        }
    }

    private static void NonNegativeInteger(JsonElement obj, string name, string parent, List<string> issues, bool allowNull)
    {
        if (!obj.TryGetProperty(name, out var value) || (allowNull && value.ValueKind == JsonValueKind.Null))
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var n) || n < 0)
        {
            issues.Add($"{parent}.{name} must be a non-negative integer.");
        }
    }

    private static bool UsesContinuationFields(JsonElement signal) =>
        signal.TryGetProperty("resumed_from_run_id", out _)
        || signal.TryGetProperty("continuation", out _)
        || (signal.TryGetProperty("self_assessment", out var sa)
            && sa.ValueKind == JsonValueKind.Object
            && sa.TryGetProperty("continuation_readiness", out _));

    private static DateTimeOffset? ReadTimestamp(JsonElement signal, List<string> issues)
    {
        var value = RequiredString(signal, "timestamp", "$", issues);
        if (value is null)
        {
            return null;
        }

        if (!Iso8601().IsMatch(value) ||
            !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            issues.Add("$.timestamp must be an ISO 8601 date and time.");
            return null;
        }

        if (parsed.Offset != TimeSpan.Zero)
        {
            issues.Add("$.timestamp must be in UTC.");
        }

        return parsed.ToUniversalTime();
    }

    private static string? RequiredString(JsonElement obj, string name, string parent, List<string> issues)
    {
        if (!obj.TryGetProperty(name, out var value))
        {
            issues.Add($"{parent}.{name} is required.");
            return null;
        }

        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            issues.Add($"{parent}.{name} must be a non-empty string.");
            return null;
        }

        return value.GetString();
    }

    private static string? OptionalString(JsonElement obj, string name, string parent, List<string> issues) =>
        obj.TryGetProperty(name, out _) ? RequiredString(obj, name, parent, issues) : null;

    private static void Score(JsonElement obj, string name, string parent, List<string> issues, bool required)
    {
        if (!obj.TryGetProperty(name, out var value))
        {
            if (required)
            {
                issues.Add($"{parent}.{name} is required (integer 1-5).");
            }

            return;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var score) || score is < 1 or > 5)
        {
            issues.Add($"{parent}.{name} must be an integer from 1 to 5.");
        }
    }

    private static void Enumerated(JsonElement obj, string name, FrozenSet<string> allowed, string parent, List<string> issues, bool required)
    {
        if (!obj.TryGetProperty(name, out var value))
        {
            if (required)
            {
                issues.Add($"{parent}.{name} is required.");
            }

            return;
        }

        if (value.ValueKind != JsonValueKind.String || !allowed.Contains(value.GetString()!))
        {
            issues.Add($"{parent}.{name} must be one of: {string.Join(", ", allowed.Order())}.");
        }
    }

    private static bool TryGetObject(JsonElement obj, string name, string parent, List<string> issues, bool required, out JsonElement value)
    {
        if (!obj.TryGetProperty(name, out value))
        {
            if (required)
            {
                issues.Add($"{parent}.{name} is required.");
            }

            return false;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            issues.Add($"{parent}.{name} must be an object.");
            return false;
        }

        return true;
    }

    internal static string SafeName(string name) =>
        name.Length <= 64 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? name : "<property>";
}
