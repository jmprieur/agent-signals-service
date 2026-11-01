using System.Collections.Frozen;

namespace AgentSignals.Api.Protocol;

/// <summary>
/// Constants from the Agent Signals protocol (SIGNAL.md, schema 0.1.x).
/// https://github.com/jennyf19/agentic-devops/blob/main/agent-signals/SIGNAL.md
/// </summary>
public static class SignalProtocol
{
    public const string CurrentSchemaVersion = "0.1.1";

    public static readonly FrozenSet<string> KnownSchemaVersions = FrozenSet.ToFrozenSet(["0.1.0", "0.1.1"]);

    public static readonly FrozenSet<string> SignalTypes =
        FrozenSet.ToFrozenSet(["execution", "outcome", "escalation", "partnership"]);

    public static readonly FrozenSet<string> Modes = FrozenSet.ToFrozenSet(["interactive", "autonomous"]);

    public static readonly FrozenSet<string> EffortBuckets =
        FrozenSet.ToFrozenSet(["none", "minimal", "moderate", "significant"]);

    public static readonly FrozenSet<string> LossBuckets = FrozenSet.ToFrozenSet(["none", "minor", "material"]);

    public static readonly FrozenSet<string> EscalationSeverities =
        FrozenSet.ToFrozenSet(["low", "medium", "high", "critical"]);

    public static readonly FrozenSet<string> ValidationStatuses =
        FrozenSet.ToFrozenSet(["outcome_validated", "self_report_only"]);

    public static readonly FrozenSet<string> EvidenceLevels = FrozenSet.ToFrozenSet(["full", "limited"]);

    /// <summary>Signals sent in protocol 0.1.x.</summary>
    public const string ProtocolFormat = "agent-signals/0.1";

    /// <summary>Signals sent in the ALAS 1.0 dialect and translated at the door.</summary>
    public const string AlasFormat = "alas/1.0";
}
