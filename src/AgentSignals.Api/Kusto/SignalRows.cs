using System.Globalization;
using AgentSignals.Api.Protocol;

namespace AgentSignals.Api.Kusto;

/// <summary>Maps a received signal to the rows it contributes to each table.</summary>
public static class SignalRows
{
    private static readonly TimeSpan FutureTolerance = TimeSpan.FromHours(1);

    public static IEnumerable<(KustoTable Table, IReadOnlyDictionary<string, object?> Row)> For(SignalRecord record)
    {
        yield return (SignalTables.Raw, Raw(record));

        if (record.IsConforming)
        {
            switch (record.Typed)
            {
                case ExecutionSignal execution:
                    yield return (SignalTables.Executions, Execution(record, execution));
                    break;
                case OutcomeSignal outcome:
                    yield return (SignalTables.Outcomes, Outcome(record, outcome));
                    break;
                case EscalationSignal escalation:
                    yield return (SignalTables.Escalations, Escalation(record, escalation));
                    break;
                case PartnershipSignal partnership:
                    yield return (SignalTables.Partnerships, Partnership(record, partnership));
                    break;
            }
        }

        foreach (var feedback in record.HumanFeedback)
        {
            yield return (SignalTables.HumanFeedback, HumanFeedback(record, feedback));
        }
    }

    public static DateTimeOffset EffectiveTime(DateTimeOffset signalTime, DateTimeOffset receivedAt) =>
        signalTime > receivedAt + FutureTolerance ? receivedAt : signalTime;

    private static Dictionary<string, object?> Raw(SignalRecord r) => new()
    {
        ["signal_id"] = r.SignalId,
        ["received_at"] = Time(r.ReceivedAt),
        ["signal_type"] = r.SignalType,
        ["source_format"] = r.SourceFormat,
        ["conforming"] = r.IsConforming,
        ["issues"] = r.Issues,
        ["warnings"] = r.Warnings,
        ["submitter"] = r.Submitter,
        ["blob_name"] = r.BlobName,
        ["run_id"] = AlasTranslator.StringOf(r.Payload["run_id"]),
        ["agent_name"] = AlasTranslator.StringOf(r.Payload["agent_name"]),
        ["skill_used"] = AlasTranslator.StringOf(r.Payload["skill_used"]),
        ["payload"] = r.Payload,
    };

    private static Dictionary<string, object?> Common(SignalRecord r, Signal s) => new()
    {
        ["signal_id"] = r.SignalId,
        ["received_at"] = Time(r.ReceivedAt),
        ["signal_time"] = Time(s.Timestamp),
        ["effective_time"] = Time(EffectiveTime(s.Timestamp, r.ReceivedAt)),
        ["source_format"] = r.SourceFormat,
        ["run_id"] = s.RunId,
        ["session_id"] = s.SessionId,
        ["agent_name"] = s.AgentName,
        ["skill_used"] = s.SkillUsed,
        ["labels"] = s.Labels,
    };

    private static Dictionary<string, object?> Execution(SignalRecord r, ExecutionSignal s)
    {
        var row = Common(r, s);
        row["mode"] = s.Mode;
        row["resumed_from_run_id"] = s.ResumedFromRunId;
        row["invoked_skills"] = s.InvokedSkills;
        row["confidence"] = s.SelfAssessment.Confidence;
        row["accuracy"] = s.SelfAssessment.Accuracy;
        row["completeness"] = s.SelfAssessment.Completeness;
        row["continuation_readiness"] = s.SelfAssessment.ContinuationReadiness;
        row["tsg_alignment"] = s.SelfAssessment.TsgAlignment;
        row["developer_experience"] = s.SelfAssessment.DeveloperExperience;
        row["what_worked"] = s.Patterns?.WhatWorked;
        row["what_was_hard"] = s.Patterns?.WhatWasHard;
        row["skill_gap"] = s.Patterns?.SkillGap;
        row["tsg_gap"] = s.Patterns?.TsgGap;
        row["improvisation"] = s.Patterns?.Improvisation;
        row["recurring_pattern"] = s.Patterns?.RecurringPattern;
        row["environment_blockers"] = s.Patterns?.EnvironmentBlockers;
        row["configured_model"] = s.Attribution?.ConfiguredModel;
        row["observed_model"] = s.Attribution?.ObservedModel;
        row["configured_reasoning_effort"] = s.Attribution?.ConfiguredReasoningEffort;
        row["observed_reasoning_effort"] = s.Attribution?.ObservedReasoningEffort;
        row["changes_applied"] = s.Result?.ChangesApplied;
        row["pr_created"] = s.Result?.PrCreated;
        row["turns_taken"] = s.Result?.TurnsTaken;
        return row;
    }

    private static Dictionary<string, object?> Outcome(SignalRecord r, OutcomeSignal s)
    {
        var row = Common(r, s);
        row["quality_rating"] = s.QualityRating;
        row["effort_to_merge"] = s.EffortToMerge;
        row["issues_found"] = s.IssuesFound;
        row["evidence_level"] = s.EvidenceLevel;
        row["recommendations_count"] = s.RecommendationsCount;
        row["recovery_rating"] = s.Continuation?.RecoveryRating;
        row["effort_to_resume"] = s.Continuation?.EffortToResume;
        row["provenance_loss"] = s.Continuation?.ProvenanceLoss;
        row["authority_loss"] = s.Continuation?.AuthorityLoss;
        row["stale_path_revived"] = s.Continuation?.StalePathRevived;
        return row;
    }

    private static Dictionary<string, object?> Escalation(SignalRecord r, EscalationSignal s)
    {
        var row = Common(r, s);
        row["reason"] = s.Reason;
        row["severity"] = s.Severity;
        row["blocking"] = s.Blocking;
        row["requested_action"] = s.RequestedAction;
        return row;
    }

    private static Dictionary<string, object?> Partnership(SignalRecord r, PartnershipSignal s)
    {
        var row = Common(r, s);
        row["skill_used"] = s.SkillUsed ?? s.ObservedSkill;
        row["observed_agent"] = s.ObservedAgent;
        row["observed_skill"] = s.ObservedSkill;
        row["signal_count_reviewed"] = s.Observation.SignalCountReviewed;
        row["time_window"] = s.Observation.TimeWindow;
        row["recommendations"] = s.Recommendations;
        row["review_confidence"] = s.SelfAssessment?.Confidence;
        row["review_completeness"] = s.SelfAssessment?.Completeness;
        row["review_note"] = s.SelfAssessment?.Note;
        return row;
    }

    private static Dictionary<string, object?> HumanFeedback(SignalRecord r, HumanFeedbackRecord f) => new()
    {
        ["signal_id"] = r.SignalId,
        ["received_at"] = Time(r.ReceivedAt),
        ["signal_time"] = Time(f.Timestamp),
        ["effective_time"] = Time(EffectiveTime(f.Timestamp, r.ReceivedAt)),
        ["source_format"] = r.SourceFormat,
        ["run_id"] = f.RunId,
        ["session_id"] = f.SessionId,
        ["agent_name"] = f.AgentName,
        ["skill_used"] = f.SkillUsed,
        ["labels"] = null,
        ["scope"] = f.Scope,
        ["rating"] = f.Rating,
        ["asked"] = f.Asked,
        ["question_id"] = f.QuestionId,
        ["variant"] = f.Variant,
    };

    private static string Time(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
}
