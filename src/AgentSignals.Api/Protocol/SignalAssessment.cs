namespace AgentSignals.Api.Protocol;

/// <summary>
/// What the service learned about a signal while checking it against the protocol.
/// A non-conforming signal is still stored: the protocol asks us to capture what happened.
/// </summary>
public sealed record SignalAssessment(
    string? SignalType,
    string? SchemaVersion,
    string? RunId,
    string? ResumedFromRunId,
    DateTimeOffset? Timestamp,
    string? AgentName,
    string? Skill,
    IReadOnlyList<string> Issues)
{
    public bool IsConforming => Issues.Count == 0;
}
