using AgentSignals.Api.Protocol;

namespace AgentSignals.Tests;

public class PrivacyScannerTests
{
    [Theory]
    [InlineData("Contact jane.doe@contoso.com for access", "email address")]
    [InlineData("token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U", "access token")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz0123456789", "GitHub token")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----", "private key")]
    [InlineData("DefaultEndpointsProtocol=https;AccountName=x;AccountKey=abc", "connection-string secret")]
    public void SecretsAndPersonalDataBlockTheSignal(string value, string kind)
    {
        var signal = Fixtures.Parse($$"""{ "patterns": { "what_was_hard": {{System.Text.Json.JsonSerializer.Serialize(value)}} } }""");

        var finding = Assert.Single(PrivacyScanner.Scan(signal));

        Assert.True(finding.Blocking);
        Assert.Equal(kind, finding.Kind);
        Assert.Equal("$.patterns.what_was_hard", finding.Path);
    }

    [Fact]
    public void UrlsAreReportedButDoNotBlock()
    {
        var signal = Fixtures.Parse("""{ "notes": ["see https://example.com/docs"] }""");

        var finding = Assert.Single(PrivacyScanner.Scan(signal));

        Assert.False(finding.Blocking);
        Assert.Equal("$.notes[0]", finding.Path);
    }

    [Theory]
    [InlineData("Updated lodash@4.17.21 and @types/node@18.x")]
    [InlineData("Transitive dependency conflict required manual resolution order.")]
    public void OrdinaryTextPasses(string value)
    {
        var signal = Fixtures.Parse($$"""{ "patterns": { "what_worked": {{System.Text.Json.JsonSerializer.Serialize(value)}} } }""");

        Assert.Empty(PrivacyScanner.Scan(signal));
    }

    [Fact]
    public void UrlInAProtocolSignalIsAWarningNotAConformanceIssue()
    {
        var json = Fixtures.ProtocolExecution().Replace("No rollback steps for multi-module projects.", "See https://example.com/tsg");

        var record = SignalRecord.FromArchived(SignalRecord.PrepareForArchive(Fixtures.Parse(json)), DateTimeOffset.UtcNow, "t");

        Assert.True(record.IsConforming);
        Assert.Single(record.Warnings);
    }
}
