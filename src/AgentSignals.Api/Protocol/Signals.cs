using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentSignals.Api.Protocol;

// Typed Agent Signals, schema 0.1.1. Members marked "extension" are additions from ALAS that
// this service types; they are candidates for the protocol, not part of it. Anything a sender
// includes beyond these members is kept in Extensions and never scored.

/// <summary>Fields common to every signal type.</summary>
public abstract record Signal
{
    public required string SignalType { get; init; }
    public required string SchemaVersion { get; init; }
    public required string RunId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string AgentName { get; init; }
    public string? SkillUsed { get; init; }

    /// <summary>Extension: groups runs from one working session.</summary>
    public string? SessionId { get; init; }

    /// <summary>Extension: free-form context such as a KPI or program identifier.</summary>
    public IReadOnlyDictionary<string, string>? Labels { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; init; }
}

/// <summary>The working agent's self-assessment after a task.</summary>
public sealed record ExecutionSignal : Signal
{
    public string? Mode { get; init; }
    public string? ResumedFromRunId { get; init; }
    public required SelfAssessment SelfAssessment { get; init; }
    public SignalPatterns? Patterns { get; init; }

    /// <summary>Extension: every skill the run loaded.</summary>
    public IReadOnlyList<string>? InvokedSkills { get; init; }

    /// <summary>Extension: which model did the work.</summary>
    public ModelAttribution? Attribution { get; init; }

    /// <summary>Extension: facts the agent reports about its own run.</summary>
    public RunResult? Result { get; init; }

    /// <summary>Extension, optional: the human's answer when the agent asked one. Autonomous runs omit it.</summary>
    public HumanFeedback? HumanFeedback { get; init; }
}

public sealed record SelfAssessment
{
    public required int Confidence { get; init; }
    public required int Accuracy { get; init; }
    public required int Completeness { get; init; }
    public int? ContinuationReadiness { get; init; }
    public int? TsgAlignment { get; init; }
    public int? DeveloperExperience { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; init; }
}

public sealed record SignalPatterns
{
    public string? WhatWorked { get; init; }
    public string? WhatWasHard { get; init; }
    public string? SkillGap { get; init; }
    public string? TsgGap { get; init; }
    public string? Improvisation { get; init; }
    public string? RecurringPattern { get; init; }
    public string? EnvironmentBlockers { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; init; }
}

public sealed record ModelAttribution
{
    public string? ConfiguredModel { get; init; }
    public string? ObservedModel { get; init; }
    public string? ConfiguredReasoningEffort { get; init; }
    public string? ObservedReasoningEffort { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; init; }
}

public sealed record RunResult
{
    public bool? ChangesApplied { get; init; }
    public bool? PrCreated { get; init; }
    public int? TurnsTaken { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; init; }
}

public sealed record HumanFeedback
{
    public int? Rating { get; init; }
    public bool? Asked { get; init; }
    public string? Scope { get; init; }
    public string? QuestionId { get; init; }
    public string? Variant { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; init; }
}

/// <summary>An independent evaluation of the work, by another agent or a human.</summary>
public sealed record OutcomeSignal : Signal
{
    public required int QualityRating { get; init; }
    public string? EffortToMerge { get; init; }
    public IReadOnlyList<string>? IssuesFound { get; init; }
    public ContinuationOutcome? Continuation { get; init; }

    /// <summary>Extension: whether the evaluator saw the full context (full) or only part of it (limited).</summary>
    public string? EvidenceLevel { get; init; }

    /// <summary>Extension.</summary>
    public int? RecommendationsCount { get; init; }
}

public sealed record ContinuationOutcome
{
    public required int RecoveryRating { get; init; }
    public string? EffortToResume { get; init; }
    public string? ProvenanceLoss { get; init; }
    public string? AuthorityLoss { get; init; }
    public bool? StalePathRevived { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; init; }
}

/// <summary>The working agent asking a human for help.</summary>
public sealed record EscalationSignal : Signal
{
    public required string Reason { get; init; }
    public required string Severity { get; init; }
    public bool? Blocking { get; init; }
    public string? RequestedAction { get; init; }
}

/// <summary>One agent reviewing another agent's signal patterns.</summary>
public sealed record PartnershipSignal : Signal
{
    public required string ObservedAgent { get; init; }
    public string? ObservedSkill { get; init; }
    public required PartnershipObservation Observation { get; init; }
    public IReadOnlyList<PartnershipRecommendation>? Recommendations { get; init; }
    public PartnershipSelfAssessment? SelfAssessment { get; init; }
}

public sealed record PartnershipObservation
{
    public required int SignalCountReviewed { get; init; }
    public required string TimeWindow { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; init; }
}

public sealed record PartnershipRecommendation
{
    public string? Type { get; init; }
    public string? Target { get; init; }
    public string? Description { get; init; }
    public string? Evidence { get; init; }
    public string? Priority { get; init; }
    public string? ValidationStatus { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; init; }
}

public sealed record PartnershipSelfAssessment
{
    public int? Confidence { get; init; }
    public int? Completeness { get; init; }
    public string? Note { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; init; }
}

/// <summary>Serializer settings for the signal wire format (snake_case, strict numbers).</summary>
public static class SignalJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Reads a conforming signal into its typed record. Returns null for an unknown type.</summary>
    public static Signal? ReadTyped(JsonElement signal, string signalType) => signalType switch
    {
        "execution" => signal.Deserialize<ExecutionSignal>(Options),
        "outcome" => signal.Deserialize<OutcomeSignal>(Options),
        "escalation" => signal.Deserialize<EscalationSignal>(Options),
        "partnership" => signal.Deserialize<PartnershipSignal>(Options),
        _ => null,
    };
}
