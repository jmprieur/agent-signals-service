using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSignals.Api.Protocol;

namespace AgentSignals.Tests;

public class AlasTranslatorTests
{
    private static readonly DateTimeOffset ReceivedAt = new(2026, 10, 2, 8, 30, 0, TimeSpan.Zero);

    private static SignalRecord Receive(string json) =>
        SignalRecord.FromArchived(SignalRecord.PrepareForArchive(Fixtures.Parse(json)), ReceivedAt, "test-client");

    [Fact]
    public void AlasExecutionBecomesAConformingTypedSignal()
    {
        var record = Receive(Fixtures.AlasExecution);

        Assert.Empty(record.Issues);
        Assert.Equal(SignalProtocol.AlasFormat, record.SourceFormat);
        var execution = Assert.IsType<ExecutionSignal>(record.Typed);
        Assert.Equal("0.1.1", execution.SchemaVersion);
        Assert.Equal("autonomous", execution.Mode);
        Assert.Equal(3, execution.SelfAssessment.Confidence);
        Assert.Equal(4, execution.SelfAssessment.DeveloperExperience);
        Assert.Equal("step one; step two", execution.Patterns!.WhatWorked);
        Assert.Equal("model-a", execution.Attribution!.ObservedModel);
        Assert.Equal(12, execution.Result!.TurnsTaken);
        Assert.Equal("KPI-1.2.3", execution.Labels!["kpi_id"]);
        Assert.Equal("SFI", execution.Labels["program"]);
        Assert.Equal(["sample-migration-skill", "alas-signal-capture"], execution.InvokedSkills);
    }

    [Fact]
    public void DevFeedbackBecomesOneHumanFeedbackRecord()
    {
        var record = Receive(Fixtures.AlasExecution);

        var feedback = Assert.Single(record.HumanFeedback);
        Assert.Equal(4, feedback.Rating);
        Assert.Equal(true, feedback.Asked);
        Assert.Equal("session", feedback.Scope);
        Assert.Equal("dedicated_skill", feedback.Variant);
        Assert.Null(record.Payload["dev_feedback"]);
    }

    [Fact]
    public void AutonomousRunWithoutHumanFeedbackStillConforms()
    {
        var signal = JsonNode.Parse(Fixtures.AlasExecution)!.AsObject();
        signal.Remove("dev_feedback");

        var record = Receive(signal.ToJsonString());

        Assert.True(record.IsConforming);
        Assert.Empty(record.HumanFeedback);
    }

    [Theory]
    [InlineData("A", 5)]
    [InlineData("B", 3)]
    [InlineData("C", 1)]
    [InlineData("a", 5)]
    public void QualityGradesMapToTheProtocolScale(string grade, int expected)
    {
        var record = Receive(Fixtures.AlasOutcome.Replace("\"quality_rating\": \"B\"", $"\"quality_rating\": \"{grade}\""));

        Assert.Equal(expected, Assert.IsType<OutcomeSignal>(record.Typed).QualityRating);
        Assert.Equal(grade, record.Payload["translation"]!["original"]!["quality_rating"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("Low", "minimal")]
    [InlineData("Medium", "moderate")]
    [InlineData("High", "significant")]
    public void EffortLevelsMapToTheProtocolBuckets(string effort, string expected)
    {
        var record = Receive(Fixtures.AlasOutcome.Replace("\"effort_to_merge\": \"Low\"", $"\"effort_to_merge\": \"{effort}\""));

        Assert.Equal(expected, Assert.IsType<OutcomeSignal>(record.Typed).EffortToMerge);
    }

    [Fact]
    public void AlasOutcomeIsTypedWithSkillLabelsAndFilledTimestamp()
    {
        var record = Receive(Fixtures.AlasOutcome);

        Assert.Empty(record.Issues);
        var outcome = Assert.IsType<OutcomeSignal>(record.Typed);
        Assert.Equal("sample-pr-quality-evaluator", outcome.AgentName);
        Assert.Equal("sample-migration-skill", outcome.SkillUsed);
        Assert.Equal("full", outcome.EvidenceLevel);
        Assert.Equal("KPI-1.2.3", outcome.Labels!["kpi_id"]);
        Assert.Equal(ReceivedAt, outcome.Timestamp);
        Assert.Contains("receive", record.Payload["translation"]!["notes"]![0]!.GetValue<string>());
        Assert.Equal(["Missing test for edge case", "Token type needs a human decision"], outcome.IssuesFound);
        Assert.Equal("Human", record.Payload["translation"]!["original"]!["issues_found"]![1]!["severity"]!.GetValue<string>());
    }

    [Fact]
    public void RepositoryUrlsAreRemovedBeforeArchiving()
    {
        var archived = SignalRecord.PrepareForArchive(Fixtures.Parse(Fixtures.AlasExecution))
            + SignalRecord.PrepareForArchive(Fixtures.Parse(Fixtures.AlasOutcome));

        Assert.DoesNotContain("pr_url", archived);
        Assert.DoesNotContain("dev.azure.com", archived);
    }

    [Fact]
    public void StandaloneHumanFeedbackIsKeptButNotTypedAsASignal()
    {
        var record = Receive(Fixtures.AlasHumanFeedback);

        Assert.False(record.IsConforming);
        Assert.Null(record.Typed);
        var feedback = Assert.Single(record.HumanFeedback);
        Assert.Equal(Fixtures.RunId, feedback.RunId);
        Assert.Equal(4, feedback.Rating);
    }

    [Fact]
    public void ProtocolSignalsAreNotTranslated()
    {
        var record = Receive(Fixtures.ProtocolExecution());

        Assert.Equal(SignalProtocol.ProtocolFormat, record.SourceFormat);
        Assert.Null(record.Payload["translation"]);
        Assert.True(record.IsConforming);
    }

    [Fact]
    public void ReplayFromTheArchiveReproducesTheSameRecord()
    {
        var archived = SignalRecord.PrepareForArchive(Fixtures.Parse(Fixtures.AlasExecution));

        var first = SignalRecord.FromArchived(archived, ReceivedAt, "a");
        var replayed = SignalRecord.FromArchived(archived, ReceivedAt, "a");

        Assert.Equal(first.SignalId, replayed.SignalId);
        Assert.Equal(first.Payload.ToJsonString(), replayed.Payload.ToJsonString());
        Assert.Equal(SignalRecord.ComputeId(archived), first.SignalId);
    }

    [Fact]
    public void ExtrasStayInTheExtensionsBag()
    {
        var execution = Assert.IsType<ExecutionSignal>(Receive(Fixtures.AlasExecution).Typed);

        Assert.True(execution.Extensions!.ContainsKey("alas_outcome"));
        Assert.True(execution.Extensions.ContainsKey("translation"));
        Assert.True(execution.Extensions["alas_outcome"].GetProperty("escalation_needed").GetBoolean());
        Assert.Equal(JsonValueKind.Undefined, execution.Extensions["alas_outcome"].TryGetProperty("pr_url", out var url) ? url.ValueKind : JsonValueKind.Undefined);
    }
}
