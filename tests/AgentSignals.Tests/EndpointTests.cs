using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace AgentSignals.Tests;

public sealed class EndpointTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public EndpointTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private Task<HttpResponseMessage> PostAsync(string json) =>
        _client.PostAsync("/v1/signals", new StringContent(json, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    [Fact]
    public async Task RootDescribesTheServiceWithoutAuthentication()
    {
        var response = await _client.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("alas/1.0", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConformingSignalIsAcceptedThenDeduplicated()
    {
        var json = Fixtures.ProtocolExecution(runId: Guid.NewGuid().ToString());

        var first = await PostAsync(json);
        var second = await PostAsync(json);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var body = await BodyAsync(first);
        var repeat = await BodyAsync(second);
        Assert.Equal("accepted", body.GetProperty("status").GetString());
        Assert.True(body.GetProperty("conforming").GetBoolean());
        Assert.Equal("duplicate", repeat.GetProperty("status").GetString());
        Assert.Equal(body.GetProperty("signal_id").GetString(), repeat.GetProperty("signal_id").GetString());
    }

    [Fact]
    public async Task SignalWithAnEmailIsRejectedAndNotStored()
    {
        var before = _factory.Archive.Blobs.Count;
        var json = Fixtures.ProtocolExecution(runId: Guid.NewGuid().ToString()).Replace("No rollback steps", "Ask jane@contoso.com about rollback steps");

        var response = await PostAsync(json);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Equal("rejected", body.GetProperty("status").GetString());
        Assert.Equal("$.patterns.tsg_gap", body.GetProperty("privacy_findings")[0].GetProperty("path").GetString());
        Assert.DoesNotContain("jane@contoso.com", body.GetRawText());
        Assert.Equal(before, _factory.Archive.Blobs.Count);
    }

    [Fact]
    public async Task AlasSignalIsTranslatedAndAccepted()
    {
        var response = await PostAsync(Fixtures.AlasOutcome.Replace(Fixtures.RunId, Guid.NewGuid().ToString()));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Equal("alas/1.0", body.GetProperty("source_format").GetString());
        Assert.True(body.GetProperty("conforming").GetBoolean());
    }

    [Fact]
    public async Task NonConformingSignalIsStoredWithItsIssues()
    {
        var response = await PostAsync(Fixtures.ProtocolExecution(confidence: 9, runId: Guid.NewGuid().ToString()));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.False(body.GetProperty("conforming").GetBoolean());
        Assert.Equal("$.self_assessment.confidence must be an integer from 1 to 5.", body.GetProperty("issues")[0].GetString());
    }

    [Fact]
    public async Task ArraysAreSubmittedOneResultPerSignal()
    {
        var json = $"[{Fixtures.ProtocolExecution(runId: Guid.NewGuid().ToString())},{Fixtures.ProtocolExecution(runId: Guid.NewGuid().ToString())}]";

        var response = await PostAsync(json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, (await BodyAsync(response)).GetProperty("results").GetArrayLength());
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    public async Task MalformedRequestsAreRejected(string json)
    {
        var response = await PostAsync(json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SchemaIsAppliedAndAcceptedSignalsReachKusto()
    {
        await PostAsync(Fixtures.ProtocolExecution(runId: Guid.NewGuid().ToString()));

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!_factory.Kusto.Commands.Any(c => c.StartsWith(".set-or-append Executions", StringComparison.Ordinal)) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.Contains(_factory.Kusto.Commands, c => c.StartsWith(".create-merge table SignalsRaw", StringComparison.Ordinal));
        Assert.Contains(_factory.Kusto.Commands, c => c.StartsWith(".set-or-append SignalsRaw", StringComparison.Ordinal));
        Assert.Contains(_factory.Kusto.Commands, c => c.StartsWith(".set-or-append Executions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InsightsRunTheMatchingFunctionWithTheSkillAsAParameter()
    {
        _factory.Kusto.Rows = [new Dictionary<string, object?> { ["skill_used"] = "sample-skill", ["paired_runs"] = 7L }];

        var response = await _client.GetAsync("/v1/insights/calibration?days=30&skill=sample-skill", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(7, (await BodyAsync(response)).GetProperty("rows")[0].GetProperty("paired_runs").GetInt32());
        var (query, parameters) = _factory.Kusto.Queries.Last(q => q.Query.Contains("SignalCalibration"));
        Assert.Contains("SignalCalibration(30d)", query);
        Assert.Equal("sample-skill", parameters["skill"]);
    }

    [Fact]
    public async Task UnknownReportIsNotFound()
    {
        var response = await _client.GetAsync("/v1/insights/everything", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

public sealed class AuthenticationStartupTests
{
    [Fact]
    public void LocalDevelopmentAuthRefusesToStartOutsideDevelopment()
    {
        using var factory = new ApiFactory { Environment = "Production" };

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("only allowed in the Development environment", error.ToString());
    }
}
