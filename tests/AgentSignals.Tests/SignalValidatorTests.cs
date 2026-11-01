using AgentSignals.Api.Protocol;

namespace AgentSignals.Tests;

public class SignalValidatorTests
{
    [Theory]
    [InlineData("execution")]
    [InlineData("outcome")]
    [InlineData("partnership")]
    public void SpecExamplesConform(string name)
    {
        var assessment = SignalValidator.Assess(Fixtures.Spec(name));

        Assert.Empty(assessment.Issues);
        Assert.Equal(name, assessment.SignalType);
    }

    [Fact]
    public void SpecEscalationExampleIsFlaggedBecauseReasonAndSeverityAreNested()
    {
        var assessment = SignalValidator.Assess(Fixtures.Spec("escalation"));

        Assert.Contains("$.reason is required at the top level (found $.escalation.reason).", assessment.Issues);
        Assert.Contains(assessment.Issues, i => i.StartsWith("$.severity is required", StringComparison.Ordinal));
    }

    [Fact]
    public void QuickstartStyleSignalWithoutTimestampIsNonConforming()
    {
        var signal = Fixtures.Parse("""
            { "signal_type": "execution", "schema_version": "0.1.0", "run_id": "ae02e3f3-42e9-43bd-ae7a-19757f5456ed",
              "agent_name": "my-agent", "self_assessment": { "accuracy": 4, "completeness": 3, "confidence": 2 } }
            """);

        Assert.Equal(["$.timestamp is required."], SignalValidator.Assess(signal).Issues);
    }

    [Theory]
    [InlineData("0", "$.self_assessment.confidence must be an integer from 1 to 5.")]
    [InlineData("6", "$.self_assessment.confidence must be an integer from 1 to 5.")]
    [InlineData("3.5", "$.self_assessment.confidence must be an integer from 1 to 5.")]
    [InlineData("\"4\"", "$.self_assessment.confidence must be an integer from 1 to 5.")]
    public void ScoresMustBeIntegersFromOneToFive(string confidence, string expected)
    {
        var signal = Fixtures.Parse(Fixtures.ProtocolExecution().Replace("\"confidence\": 4", $"\"confidence\": {confidence}"));

        Assert.Contains(expected, SignalValidator.Assess(signal).Issues);
    }

    [Theory]
    [InlineData("\"run_id\": \"ae02e3f3-42e9-43bd-ae7a-19757f5456ed\"", "\"run_id\": \"gh_742918233\"", "$.run_id must be a UUID.")]
    [InlineData("2026-10-01T12:00:00Z", "2026-10-01T12:00:00+02:00", "$.timestamp must be in UTC.")]
    [InlineData("2026-10-01T12:00:00Z", "yesterday", "$.timestamp must be an ISO 8601 date and time.")]
    [InlineData("\"agent_name\": \"sample-agent\",", "", "$.agent_name is required.")]
    [InlineData("\"0.1.1\"", "\"2.0\"", "$.schema_version is not a known version (known: 0.1.0, 0.1.1).")]
    public void CommonFieldContracts(string find, string replace, string expected)
    {
        var signal = Fixtures.Parse(Fixtures.ProtocolExecution().Replace(find, replace));

        Assert.Contains(expected, SignalValidator.Assess(signal).Issues);
    }

    [Fact]
    public void ContinuationFieldsRequireSchema011()
    {
        var signal = Fixtures.Parse(Fixtures.ProtocolExecution()
            .Replace("\"0.1.1\"", "\"0.1.0\"")
            .Replace("\"completeness\": 3", "\"completeness\": 3, \"continuation_readiness\": 4"));

        Assert.Equal(["Continuation fields require schema_version 0.1.1."], SignalValidator.Assess(signal).Issues);
    }

    [Fact]
    public void UnknownSignalTypeIsNonConforming()
    {
        var signal = Fixtures.Parse(Fixtures.ProtocolExecution().Replace("\"execution\"", "\"test_harness\""));

        Assert.Contains("$.signal_type must be one of: execution, outcome, escalation, partnership.", SignalValidator.Assess(signal).Issues);
    }

    [Fact]
    public void IssuesNeverEchoSubmittedValues()
    {
        var signal = Fixtures.Parse(Fixtures.ProtocolExecution().Replace("\"sample-agent\"", "{\"secret-ish\": 1}"));

        Assert.All(SignalValidator.Assess(signal).Issues, issue => Assert.DoesNotContain("secret-ish", issue));
    }
}
