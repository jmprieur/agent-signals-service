using System.Text.Json;

namespace AgentSignals.Tests;

internal static class Fixtures
{
    public static JsonElement Spec(string name) =>
        Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "spec", $"{name}.json")));

    public static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public const string RunId = "ae02e3f3-42e9-43bd-ae7a-19757f5456ed";

    /// <summary>Synthetic ALAS 1.0 execution signal, shaped like the fields observed in production.</summary>
    public const string AlasExecution = """
        {
          "schema_version": "1.0",
          "signal_type": "execution",
          "run_id": "ae02e3f3-42e9-43bd-ae7a-19757f5456ed",
          "session_id": "11111111-2222-3333-4444-555555555555",
          "timestamp": "2026-10-01T12:00:00Z",
          "agent_name": "sample-migration-agent",
          "skill_used": "sample-migration-skill",
          "invoked_skills": ["sample-migration-skill", "alas-signal-capture"],
          "mode": "automated",
          "kpi": { "id": "KPI-1.2.3", "name": "Sample KPI", "program": "SFI" },
          "self_assessment": { "accuracy": 4, "completeness": 5, "confidence": "3", "tsg_alignment": 2, "developer_experience": { "score": 4, "comment": "ok" } },
          "patterns": { "what_worked": ["step one", "step two"], "what_was_hard": "transitive conflict", "skill_gap": null, "tsg_gap": "", "improvisation": "", "recurring_pattern": "", "environment_blockers": "" },
          "execution_attribution": { "configured_model": "model-a", "observed_model": "model-a", "configured_reasoning_effort": "high", "observed_reasoning_effort": null },
          "outcome": { "changes_applied": true, "pr_created": true, "turns_taken": 12, "escalation_needed": true, "pr_url": "https://dev.azure.com/org/project/_git/repo/pullrequest/1" },
          "dev_feedback": { "asked": true, "rating": 4, "rating_scale": "1-5", "question_id": "human_feedback_v1", "question_scope": "session", "arm": "dedicated_skill", "prompt_variant": "v1", "freetext_routed": "none" }
        }
        """;

    /// <summary>Synthetic ALAS 1.0 outcome signal, without a timestamp (74% of production outcomes).</summary>
    public const string AlasOutcome = """
        {
          "schema_version": "1.0",
          "signal_type": "outcome",
          "run_id": "ae02e3f3-42e9-43bd-ae7a-19757f5456ed",
          "agent_name": "sample-pr-quality-evaluator",
          "invoked_skills": ["sample-pr-quality-evaluator-skill"],
          "evaluator": "sample-pr-quality-evaluator-skill",
          "quality_rating": "B",
          "effort_to_merge": "Low",
          "issues_found": [{ "issue": "Missing test for edge case", "severity": "Medium", "risk": "Regression" }, { "description": "Token type needs a human decision", "severity": "Human" }],
          "recommendations_count": 1,
          "context_level": "full",
          "skill_context": { "agent_used": "sample-migration-agent", "skill_used": "sample-migration-skill", "goal": "migrate", "kpi_id": "KPI-1.2.3", "kpi_name": "Sample KPI", "program": "SFI" },
          "pr_url": "https://dev.azure.com/org/project/_git/repo/pullrequest/1"
        }
        """;

    /// <summary>Synthetic standalone ALAS human-feedback signal.</summary>
    public const string AlasHumanFeedback = """
        {
          "schema": "human_feedback_v1",
          "signal_type": "human-feedback",
          "run_id": "ae02e3f3-42e9-43bd-ae7a-19757f5456ed",
          "timestamp": "2026-10-01T12:05:00Z",
          "agent_name": "sample-migration-agent",
          "invoked_skills": ["sample-migration-skill"],
          "question_id": "human_feedback_v1",
          "question_scope": "session",
          "arm": "dedicated_skill",
          "prompt_variant": "v1",
          "rating": 4,
          "rating_scale": "1-5",
          "freetext": null
        }
        """;

    public static string ProtocolExecution(int confidence = 4, string runId = RunId) => $$"""
        {
          "signal_type": "execution",
          "schema_version": "0.1.1",
          "run_id": "{{runId}}",
          "timestamp": "2026-10-01T12:00:00Z",
          "agent_name": "sample-agent",
          "skill_used": "sample-skill",
          "self_assessment": { "confidence": {{confidence}}, "accuracy": 4, "completeness": 3 },
          "patterns": { "tsg_gap": "No rollback steps for multi-module projects." }
        }
        """;
}
