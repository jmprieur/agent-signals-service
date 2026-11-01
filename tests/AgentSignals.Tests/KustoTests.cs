using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentSignals.Api.Kusto;
using AgentSignals.Api.Protocol;

namespace AgentSignals.Tests;

public partial class KustoTests
{
    [GeneratedRegex("base64_decode_tostring\\(\"([A-Za-z0-9+/=]*)\"\\)")]
    private static partial Regex Base64Payload();

    private static SignalRecord Receive(string json) =>
        SignalRecord.FromArchived(SignalRecord.PrepareForArchive(Fixtures.Parse(json)), DateTimeOffset.UtcNow, "t");

    [Fact]
    public void SubmittedValuesNeverAppearAsKql()
    {
        const string hostile = "\")) | .drop table Executions //";
        var record = Receive(Fixtures.ProtocolExecution().Replace("sample-agent", hostile.Replace("\"", "\\\"")));

        var (table, row) = SignalRows.For(record).Single(r => r.Table == SignalTables.Executions);
        var command = table.AppendCommand([row]);

        Assert.DoesNotContain(".drop", command);
        var payload = Encoding.UTF8.GetString(Convert.FromBase64String(Base64Payload().Match(command).Groups[1].Value));
        Assert.Contains(".drop table", payload);
    }

    [Theory]
    [InlineData("execution")]
    [InlineData("outcome")]
    [InlineData("partnership")]
    public void RowsMatchTheirTableColumnsExactly(string specExample)
    {
        var record = SignalRecord.FromArchived(
            SignalRecord.PrepareForArchive(Fixtures.Spec(specExample)), DateTimeOffset.UtcNow, "t");

        foreach (var (table, row) in SignalRows.For(record))
        {
            Assert.Equal(table.Columns.Select(c => c.Name).Order(), row.Keys.Order());
        }
    }

    [Fact]
    public void AlasExecutionProducesRawExecutionAndHumanFeedbackRows()
    {
        var tables = SignalRows.For(Receive(Fixtures.AlasExecution)).Select(r => r.Table.Name);

        Assert.Equal(["SignalsRaw", "Executions", "HumanFeedback"], tables);
    }

    [Fact]
    public void NonConformingSignalsOnlyReachTheRawTable()
    {
        var tables = SignalRows.For(Receive(Fixtures.ProtocolExecution(confidence: 9))).Select(r => r.Table.Name);

        Assert.Equal(["SignalsRaw"], tables);
    }

    [Fact]
    public void FutureTimestampsFallBackToReceiptTime()
    {
        var received = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(received, SignalRows.EffectiveTime(received.AddDays(30), received));
        Assert.Equal(received.AddMinutes(-5), SignalRows.EffectiveTime(received.AddMinutes(-5), received));
    }

    [Fact]
    public void SchemaHasTablesThenFunctions()
    {
        var commands = Api.Kusto.KustoSchemaInitializer.Commands();

        Assert.Equal(SignalTables.All.Count, commands.Count(c => c.StartsWith(".create-merge table", StringComparison.Ordinal)));
        var functions = commands.Where(c => c.StartsWith(".create-or-alter function", StringComparison.Ordinal)).ToList();
        Assert.Equal(commands.Count - SignalTables.All.Count, functions.Count);
        Assert.Contains(functions, f => f.Contains("SignalCalibration(window: timespan)"));
        Assert.All(Api.Insights.SignalInsights.Reports, r => Assert.Contains(functions, f => f.Contains($"\n{r.Function}(window: timespan)")));
    }

    [Fact]
    public void InsightQueriesAreBoundedAndParameterized()
    {
        var report = Api.Insights.SignalInsights.Find("calibration")!;

        var query = Api.Insights.SignalInsights.BuildQuery(report, days: 365, top: 10_000);

        Assert.Contains("SignalCalibration(90d)", query);
        Assert.EndsWith("| take 200", query);
        Assert.Contains("skill_used == skill", query);
    }
}
