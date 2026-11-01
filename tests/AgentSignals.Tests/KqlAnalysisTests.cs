using System.Text.RegularExpressions;
using AgentSignals.Api.Insights;
using AgentSignals.Api.Kusto;
using Kusto.Language;
using Kusto.Language.Symbols;

namespace AgentSignals.Tests;

/// <summary>
/// Checks every KQL statement with the official Kusto language analyzer against the table
/// definitions in SignalTables, so syntax errors, unknown columns and type mismatches fail here
/// rather than at deployment. This does not execute the queries.
/// </summary>
public partial class KqlAnalysisTests
{
    [GeneratedRegex(@"^\.create-or-alter function with \(.*?\)\s*\r?\n(?<name>\w+)\((?<parameters>[^)]*)\)\s*\{(?<body>.*)\}\s*$", RegexOptions.Singleline)]
    private static partial Regex FunctionCommand();

    private sealed record KqlFunction(string Name, string Parameters, string Body);

    private static IReadOnlyList<KqlFunction> Functions() =>
        KustoSchemaInitializer.FunctionCommands()
            .Select(command => FunctionCommand().Match(command))
            .Select(m =>
            {
                Assert.True(m.Success, "Every schema function must follow the expected command layout.");
                return new KqlFunction(m.Groups["name"].Value, m.Groups["parameters"].Value, m.Groups["body"].Value);
            })
            .ToList();

    private static GlobalState Globals(IEnumerable<KqlFunction> functions)
    {
        var symbols = new List<Symbol>();
        symbols.AddRange(SignalTables.All.Select(t =>
            new TableSymbol(t.Name, "(" + string.Join(", ", t.Columns.Select(c => $"{c.Name}: {c.Type}")) + ")")));
        symbols.AddRange(functions.Select(f => new FunctionSymbol(f.Name, $"({f.Parameters})", $"{{{f.Body}}}")));
        return GlobalState.Default.WithDatabase(new DatabaseSymbol("signals", symbols));
    }

    private static void AssertNoErrors(string kql, GlobalState globals, string label)
    {
        var errors = KustoCode.ParseAndAnalyze(kql, globals).GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => $"{label}: {d.Message} near '{Excerpt(kql, d.Start)}'")
            .ToList();

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    private static string Excerpt(string text, int start) =>
        text.Substring(Math.Max(0, start - 20), Math.Min(60, text.Length - Math.Max(0, start - 20))).ReplaceLineEndings(" ");

    public static TheoryData<string> FunctionNames() => [.. Functions().Select(f => f.Name)];

    [Theory]
    [MemberData(nameof(FunctionNames))]
    public void SchemaFunctionBodiesAnalyzeWithoutErrors(string name)
    {
        var functions = Functions();
        var function = functions.Single(f => f.Name == name);
        var parameters = string.Join("\n", function.Parameters.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split(':')[0].Trim())
            .Select(p => $"let {p} = 7d;"));

        AssertNoErrors($"{parameters}\n{function.Body}", Globals(functions.Where(f => f.Name != name)), name);
    }

    public static TheoryData<string> ReportNames() => [.. SignalInsights.Reports.Select(r => r.Name)];

    [Theory]
    [MemberData(nameof(ReportNames))]
    public void InsightQueriesAnalyzeWithoutErrors(string report)
    {
        var query = SignalInsights.BuildQuery(SignalInsights.Find(report)!, days: 7, top: 50);

        AssertNoErrors(query, Globals(Functions()), report);
    }

    [Fact]
    public void LoadQueriesAnalyzeWithoutErrors()
    {
        foreach (var table in SignalTables.All)
        {
            var command = table.AppendCommand([table.Columns.ToDictionary(c => c.Name, c => (object?)null)]);
            var query = command[(command.IndexOf("<|", StringComparison.Ordinal) + 2)..];

            AssertNoErrors(query, Globals([]), table.Name);
        }
    }
}
