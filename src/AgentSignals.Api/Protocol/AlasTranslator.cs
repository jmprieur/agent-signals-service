using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentSignals.Api.Protocol;

/// <summary>A human's rating for a run, merged from an execution signal or a standalone ALAS human-feedback signal.</summary>
public sealed record HumanFeedbackRecord(
    string RunId,
    string? SessionId,
    string? AgentName,
    string? SkillUsed,
    DateTimeOffset Timestamp,
    string? Scope,
    int? Rating,
    bool? Asked,
    string? QuestionId,
    string? Variant);

/// <summary>Result of translating an ALAS 1.0 signal to protocol 0.1.1.</summary>
public sealed record AlasTranslation(JsonObject Signal, HumanFeedbackRecord? StandaloneHumanFeedback);

/// <summary>
/// Translates the ALAS 1.0 dialect to Agent Signals 0.1.1 at the door. The mapping is documented in
/// docs/alas-translation.md. Original values are kept under "translation" in the stored signal so
/// every converted value can be checked and the mapping revised.
/// </summary>
public static class AlasTranslator
{
    private static readonly Dictionary<string, int> QualityGrades = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = 5,
        ["B"] = 3,
        ["C"] = 1,
    };

    private static readonly Dictionary<string, string> EffortLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["low"] = "minimal",
        ["medium"] = "moderate",
        ["high"] = "significant",
    };

    private static readonly string[] SelfAssessmentScores =
        ["confidence", "accuracy", "completeness", "continuation_readiness", "tsg_alignment", "developer_experience"];

    /// <summary>
    /// ALAS signals declare schema_version 1.x; protocol signals declare 0.1.x. ALAS human-feedback
    /// signals carry no schema_version at all, so they are recognized by their signal type.
    /// </summary>
    public static bool IsAlas(JsonObject signal) =>
        StringOf(signal["signal_type"]) == "human-feedback" || signal["schema_version"] switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s == "1" || s.StartsWith("1.", StringComparison.Ordinal),
            JsonValue v when v.TryGetValue<double>(out var d) => d is >= 1 and < 2,
            _ => false,
        };

    /// <summary>Removes fields that must never be stored. Applied before the signal is archived.</summary>
    public static void RemoveForbiddenFields(JsonObject signal)
    {
        signal.Remove("pr_url");
        if (signal["outcome"] is JsonObject outcome)
        {
            outcome.Remove("pr_url");
        }
    }

    public static AlasTranslation Translate(JsonObject archived, DateTimeOffset receivedAt)
    {
        var signal = (JsonObject)archived.DeepClone();
        var original = new JsonObject();
        var notes = new JsonArray();

        Move(signal, "schema_version", original);
        signal["schema_version"] = SignalProtocol.CurrentSchemaVersion;

        if (string.IsNullOrWhiteSpace(StringOf(signal["timestamp"])))
        {
            signal["timestamp"] = receivedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            notes.Add(JsonValue.Create("timestamp was missing and was set to the time the service received the signal"));
        }

        MoveKpiToLabels(signal);

        var signalType = StringOf(signal["signal_type"]);
        HumanFeedbackRecord? standalone = null;
        switch (signalType)
        {
            case "execution":
                TranslateExecution(signal, original, notes);
                break;
            case "outcome":
                TranslateOutcome(signal, original);
                break;
            case "human-feedback":
                standalone = ReadStandaloneHumanFeedback(signal, receivedAt);
                break;
        }

        signal["translation"] = new JsonObject
        {
            ["source_format"] = SignalProtocol.AlasFormat,
            ["original"] = original,
            ["notes"] = notes,
        };

        return new AlasTranslation(signal, standalone);
    }

    private static void TranslateExecution(JsonObject signal, JsonObject original, JsonArray notes)
    {
        var mode = StringOf(signal["mode"]);
        if (mode is "automated" or "service")
        {
            Move(signal, "mode", original);
            signal["mode"] = "autonomous";
        }

        if (signal["self_assessment"] is JsonObject assessment)
        {
            foreach (var name in SelfAssessmentScores)
            {
                var node = assessment[name];
                if (node is JsonObject { } scored && scored["score"] is JsonValue score)
                {
                    assessment[name] = score.DeepClone();
                    notes.Add(JsonValue.Create($"self_assessment.{name} was an object; its score was kept"));
                }
                else if (node is JsonValue v && v.TryGetValue<string>(out var text) &&
                         int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    assessment[name] = parsed;
                }
            }
        }

        if (signal["patterns"] is JsonObject patterns)
        {
            foreach (var (name, value) in patterns.ToList())
            {
                if (value is null)
                {
                    patterns.Remove(name);
                }
                else if (value is JsonArray items)
                {
                    patterns[name] = string.Join("; ", items.Select(StringOf).Where(s => !string.IsNullOrWhiteSpace(s)));
                }
            }
        }

        Rename(signal, "execution_attribution", "attribution");

        if (signal["result"] is not null and not JsonObject)
        {
            Rename(signal, "result", "alas_result");
        }

        if (signal["outcome"] is JsonObject selfReported)
        {
            var result = new JsonObject();
            CopyIf<bool>(selfReported, "changes_applied", result);
            CopyIf<bool>(selfReported, "pr_created", result);
            CopyIf<int>(selfReported, "turns_taken", result);
            if (result.Count > 0)
            {
                signal["result"] = result;
            }

            // The rest stays as an extension under a name that cannot be confused with outcome signals.
            Rename(signal, "outcome", "alas_outcome");
        }

        if (signal["dev_feedback"] is JsonObject devFeedback)
        {
            // "dev" is the agent: this is the human's answer to a question the agent asked about its run.
            var feedback = (JsonObject)devFeedback.DeepClone();
            Rename(feedback, "question_scope", "scope");
            Rename(feedback, "arm", "variant");
            if (feedback["rating"] is null)
            {
                feedback.Remove("rating");
            }

            signal.Remove("dev_feedback");
            signal["human_feedback"] = feedback;
        }
    }

    private static void TranslateOutcome(JsonObject signal, JsonObject original)
    {
        var grade = StringOf(signal["quality_rating"])?.Trim();
        if (grade is not null)
        {
            if (QualityGrades.TryGetValue(grade, out var rating))
            {
                Move(signal, "quality_rating", original);
                signal["quality_rating"] = rating;
            }
            else if (int.TryParse(grade, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
            {
                signal["quality_rating"] = numeric;
            }
        }

        var effort = StringOf(signal["effort_to_merge"])?.Trim();
        if (effort is not null && EffortLevels.TryGetValue(effort, out var bucket))
        {
            Move(signal, "effort_to_merge", original);
            signal["effort_to_merge"] = bucket;
        }

        Rename(signal, "context_level", "evidence_level");

        // ALAS issues are objects ({issue|description, severity, risk}); the protocol expects text.
        // Keep the text and preserve the structured list under translation.original.
        if (signal["issues_found"] is JsonArray issues && issues.Any(i => i is JsonObject))
        {
            Move(signal, "issues_found", original);
            var texts = new JsonArray();
            foreach (var item in issues)
            {
                var text = item is JsonObject issue
                    ? StringOf(issue["issue"]) ?? StringOf(issue["description"]) ?? StringOf(issue["category"])
                    : StringOf(item);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    texts.Add(JsonValue.Create(text));
                }
            }

            signal["issues_found"] = texts;
        }

        if (signal["skill_context"] is JsonObject context &&
            string.IsNullOrWhiteSpace(StringOf(signal["skill_used"])) &&
            StringOf(context["skill_used"]) is { Length: > 0 } skill)
        {
            signal["skill_used"] = skill;
        }
    }

    private static HumanFeedbackRecord? ReadStandaloneHumanFeedback(JsonObject signal, DateTimeOffset receivedAt)
    {
        var runId = StringOf(signal["run_id"]);
        if (string.IsNullOrWhiteSpace(runId))
        {
            return null;
        }

        int? rating = signal["rating"] is JsonValue r && r.TryGetValue<int>(out var value) && value is >= 1 and <= 5 ? value : null;
        var timestamp = DateTimeOffset.TryParse(StringOf(signal["timestamp"]), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t.ToUniversalTime()
            : receivedAt;

        return new HumanFeedbackRecord(
            runId,
            StringOf(signal["session_id"]),
            StringOf(signal["agent_name"]),
            StringOf(signal["skill_used"]),
            timestamp,
            StringOf(signal["question_scope"]),
            rating,
            Asked: true,
            StringOf(signal["question_id"]),
            StringOf(signal["arm"]));
    }

    private static void MoveKpiToLabels(JsonObject signal)
    {
        var labels = signal["labels"] as JsonObject ?? new JsonObject();

        if (signal["kpi"] is JsonObject kpi)
        {
            AddLabel(labels, "kpi_id", kpi["id"]);
            AddLabel(labels, "kpi_name", kpi["name"]);
            AddLabel(labels, "program", kpi["program"]);
            signal.Remove("kpi");
        }

        foreach (var legacy in new[] { "kpi_id", "kpi_name", "program" })
        {
            if (signal.ContainsKey(legacy))
            {
                AddLabel(labels, legacy, signal[legacy]);
                signal.Remove(legacy);
            }
        }

        if (signal["skill_context"] is JsonObject context)
        {
            AddLabel(labels, "kpi_id", context["kpi_id"]);
            AddLabel(labels, "kpi_name", context["kpi_name"]);
            AddLabel(labels, "program", context["program"]);
        }

        if (labels.Count > 0)
        {
            signal["labels"] = labels;
        }
    }

    private static void AddLabel(JsonObject labels, string name, JsonNode? value)
    {
        var text = StringOf(value);
        if (!string.IsNullOrWhiteSpace(text) && !labels.ContainsKey(name))
        {
            labels[name] = text;
        }
    }

    private static void CopyIf<T>(JsonObject source, string name, JsonObject target)
    {
        if (source[name] is JsonValue value && value.TryGetValue<T>(out _))
        {
            target[name] = value.DeepClone();
        }
    }

    private static void Move(JsonObject signal, string name, JsonObject original)
    {
        if (signal[name] is { } value)
        {
            original[name] = value.DeepClone();
        }
    }

    private static void Rename(JsonObject obj, string from, string to)
    {
        if (obj.ContainsKey(from) && !obj.ContainsKey(to))
        {
            var value = obj[from];
            obj.Remove(from);
            obj[to] = value;
        }
    }

    internal static string? StringOf(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.GetValueKind() is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToJsonString(),
        _ => null,
    };
}
