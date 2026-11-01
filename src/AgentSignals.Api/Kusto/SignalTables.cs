using System.Text;
using System.Text.Json;

namespace AgentSignals.Api.Kusto;

public sealed record KustoColumn(string Name, string Type);

/// <summary>
/// A typed Kusto table. The same definition creates the table and builds the command that loads
/// rows into it, so the table schema and the code cannot drift apart.
/// </summary>
public sealed record KustoTable(string Name, IReadOnlyList<KustoColumn> Columns)
{
    public string CreateMergeCommand =>
        $".create-merge table {Name} ({string.Join(", ", Columns.Select(c => $"{c.Name}: {c.Type}"))})";

    /// <summary>
    /// Builds a ".set-or-append" command. Rows travel as base64-encoded JSON, so no submitted value is
    /// ever interpreted as KQL. This works on the Kusto emulator and on Azure Data Explorer alike.
    /// </summary>
    public string AppendCommand(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(rows)));
        var projections = string.Join(",\n    ", Columns.Select(c => $"{c.Name} = {Conversion(c)}"));
        return $"""
            .set-or-append {Name} <|
            print rows = parse_json(base64_decode_tostring("{payload}"))
            | mv-expand r = rows
            | project
                {projections}
            """;
    }

    private static string Conversion(KustoColumn column)
    {
        var value = $"r['{column.Name}']";
        return column.Type switch
        {
            "string" => $"tostring({value})",
            "datetime" => $"todatetime({value})",
            "int" => $"toint({value})",
            "long" => $"tolong({value})",
            "real" => $"toreal({value})",
            "bool" => $"tobool({value})",
            "dynamic" => value,
            _ => throw new NotSupportedException($"Unsupported Kusto column type '{column.Type}'."),
        };
    }
}

/// <summary>The tables this service owns.</summary>
public static class SignalTables
{
    private static KustoColumn S(string name) => new(name, "string");
    private static KustoColumn I(string name) => new(name, "int");
    private static KustoColumn B(string name) => new(name, "bool");
    private static KustoColumn D(string name) => new(name, "dynamic");
    private static KustoColumn T(string name) => new(name, "datetime");

    // Every typed table starts with these columns. effective_time is the signal's own time,
    // or the receipt time when the signal claims a time more than an hour in the future.
    private static readonly KustoColumn[] Common =
    [
        S("signal_id"), T("received_at"), T("signal_time"), T("effective_time"), S("source_format"),
        S("run_id"), S("session_id"), S("agent_name"), S("skill_used"), D("labels"),
    ];

    /// <summary>Every accepted signal, conforming or not, as translated. Holds the extensions bag.</summary>
    public static readonly KustoTable Raw = new("SignalsRaw",
    [
        S("signal_id"), T("received_at"), S("signal_type"), S("source_format"), B("conforming"),
        D("issues"), D("warnings"), S("submitter"), S("blob_name"), S("run_id"), S("agent_name"),
        S("skill_used"), D("payload"),
    ]);

    public static readonly KustoTable Executions = new("Executions",
    [
        .. Common,
        S("mode"), S("resumed_from_run_id"), D("invoked_skills"),
        I("confidence"), I("accuracy"), I("completeness"), I("continuation_readiness"),
        I("tsg_alignment"), I("developer_experience"),
        S("what_worked"), S("what_was_hard"), S("skill_gap"), S("tsg_gap"), S("improvisation"),
        S("recurring_pattern"), S("environment_blockers"),
        S("configured_model"), S("observed_model"), S("configured_reasoning_effort"), S("observed_reasoning_effort"),
        B("changes_applied"), B("pr_created"), I("turns_taken"),
    ]);

    public static readonly KustoTable Outcomes = new("Outcomes",
    [
        .. Common,
        I("quality_rating"), S("effort_to_merge"), D("issues_found"), S("evidence_level"), I("recommendations_count"),
        I("recovery_rating"), S("effort_to_resume"), S("provenance_loss"), S("authority_loss"), B("stale_path_revived"),
    ]);

    public static readonly KustoTable Escalations = new("Escalations",
    [
        .. Common,
        S("reason"), S("severity"), B("blocking"), S("requested_action"),
    ]);

    public static readonly KustoTable Partnerships = new("Partnerships",
    [
        .. Common,
        S("observed_agent"), S("observed_skill"), I("signal_count_reviewed"), S("time_window"),
        D("recommendations"), I("review_confidence"), I("review_completeness"), S("review_note"),
    ]);

    public static readonly KustoTable HumanFeedback = new("HumanFeedback",
    [
        .. Common,
        S("scope"), I("rating"), B("asked"), S("question_id"), S("variant"),
    ]);

    public static readonly IReadOnlyList<KustoTable> All = [Raw, Executions, Outcomes, Escalations, Partnerships, HumanFeedback];
}
