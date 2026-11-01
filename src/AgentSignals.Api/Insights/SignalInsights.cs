using AgentSignals.Api.Kusto;

namespace AgentSignals.Api.Insights;

/// <summary>A named report backed by one Kusto function.</summary>
public sealed record InsightReport(string Name, string Function, bool FiltersBySkill, string Description);

/// <summary>
/// Read side of the loop. Every report is bounded (days and row caps), following the protocol's
/// consumption rule: synthesize, bound and rank, never replay the whole backlog.
/// </summary>
public sealed class SignalInsights(IKustoGateway kusto)
{
    public const int MaxDays = 90;
    public const int MaxRows = 200;

    public static readonly IReadOnlyList<InsightReport> Reports =
    [
        new("overview", "SignalOverview", false, "Counts for the window, including executions no outcome has verified yet."),
        new("skill-health", "SignalSkillHealth", true, "Per-skill health findings (closing-the-loop.md section 2.2)."),
        new("calibration", "SignalCalibration", true, "Trust equation per agent and skill: 5 - |confidence - quality_rating|."),
        new("model-calibration", "SignalModelCalibration", false, "Trust equation per observed model."),
        new("continuation", "SignalContinuationGap", true, "Handoff readiness claimed versus recovery rated."),
        new("patterns", "SignalPatternCandidates", false, "Recurring pattern values checked against the protocol thresholds."),
        new("pattern-evidence", "SignalPatternEvidence", true, "Raw pattern values for a synthesizer agent to cluster."),
        new("escalations", "SignalEscalations", true, "Escalations, newest first."),
        new("recommendations", "SignalRecommendations", true, "Partnership recommendations, outcome-validated evidence first."),
        new("human-feedback", "SignalHumanFeedbackSummary", true, "Optional human ratings next to the agent's own confidence."),
    ];

    public static InsightReport? Find(string name) =>
        Reports.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

    public static string BuildQuery(InsightReport report, int days, int top)
    {
        days = Math.Clamp(days, 1, MaxDays);
        top = Math.Clamp(top, 1, MaxRows);
        var filter = report.FiltersBySkill ? "\n| where isempty(skill) or skill_used == skill" : string.Empty;
        return $"declare query_parameters(skill: string = \"\");\n{report.Function}({days}d){filter}\n| take {top}";
    }

    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> RunAsync(
        InsightReport report, int days, int top, string? skill, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string>();
        if (report.FiltersBySkill && !string.IsNullOrWhiteSpace(skill))
        {
            parameters["skill"] = skill;
        }

        return kusto.QueryAsync(BuildQuery(report, days, top), parameters, cancellationToken);
    }
}
