using System.Data;
using System.Text.Json.Nodes;
using Azure.Core;
using Kusto.Data;
using Kusto.Data.Common;
using Kusto.Data.Net.Client;

namespace AgentSignals.Api.Kusto;

/// <summary>The few Kusto operations this service needs. Faked in tests.</summary>
public interface IKustoGateway
{
    Task ExecuteCommandAsync(string command, CancellationToken cancellationToken);

    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(
        string query, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken);
}

/// <summary>
/// Kusto client for both the local emulator (plain HTTP, no authentication) and Azure Data Explorer
/// (HTTPS with an Entra token from the app's managed identity).
/// </summary>
public sealed class KustoGateway : IKustoGateway, IDisposable
{
    private readonly ICslAdminProvider _admin;
    private readonly ICslQueryProvider _query;
    private readonly string _database;
    private readonly ILogger<KustoGateway> _logger;

    public KustoGateway(string connectionString, TokenCredential credential, ILogger<KustoGateway> logger)
    {
        var builder = new KustoConnectionStringBuilder(connectionString);
        _database = builder.InitialCatalog;
        if (string.IsNullOrWhiteSpace(_database))
        {
            throw new InvalidOperationException("The Kusto connection string must name a database (Initial Catalog).");
        }

        if (builder.DataSource.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            builder = builder.WithAadAzureTokenCredentialsAuthentication(credential);
        }

        _admin = KustoClientFactory.CreateCslAdminProvider(builder);
        _query = KustoClientFactory.CreateCslQueryProvider(builder);
        _logger = logger;
    }

    public async Task ExecuteCommandAsync(string command, CancellationToken cancellationToken)
    {
        var properties = NewProperties();
        try
        {
            using var reader = await _admin.ExecuteControlCommandAsync(_database, command, properties).WaitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Kusto command failed. ClientRequestId {ClientRequestId}", properties.ClientRequestId);
            throw;
        }
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(
        string query, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        var properties = NewProperties();
        foreach (var (name, value) in parameters)
        {
            properties.SetParameter(name, value);
        }

        try
        {
            using var reader = await _query.ExecuteQueryAsync(_database, query, properties, cancellationToken);
            return Read(reader);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Kusto query failed. ClientRequestId {ClientRequestId}", properties.ClientRequestId);
            throw;
        }
    }

    public void Dispose()
    {
        _admin.Dispose();
        _query.Dispose();
    }

    private static ClientRequestProperties NewProperties() => new()
    {
        ClientRequestId = $"AgentSignals;{Guid.NewGuid()}",
    };

    private static List<IReadOnlyDictionary<string, object?>> Read(IDataReader reader)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(reader.FieldCount);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = Normalize(reader.GetValue(i));
            }

            rows.Add(row);
        }

        return rows;
    }

    // Dynamic columns come back as SDK-specific JSON objects; convert them to System.Text.Json nodes.
    private static object? Normalize(object? value) => value switch
    {
        null or DBNull => null,
        string or bool or int or long or double or decimal or DateTime or TimeSpan or Guid => value,
        SByte b => (int)b,
        _ => JsonNode.Parse(value.ToString() ?? "null"),
    };
}
