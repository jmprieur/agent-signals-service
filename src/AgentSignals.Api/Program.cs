using System.Threading.RateLimiting;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentSignals.Api.Auth;
using AgentSignals.Api.Endpoints;
using AgentSignals.Api.Ingestion;
using AgentSignals.Api.Insights;
using AgentSignals.Api.Kusto;
using AgentSignals.Api.Mcp;
using AgentSignals.Api.Storage;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddAzureBlobContainerClient("signals");
builder.AddSignalAuthentication();

builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 1_048_576);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddProblemDetails();
builder.Services.AddHttpContextAccessor();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("signals", context => RateLimitPartition.GetFixedWindowLimiter(
        SignalAuthentication.Submitter(context.User),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

// Kusto: emulator locally (plain HTTP), Azure Data Explorer when deployed (managed identity).
var kustoConnectionString = builder.Configuration.GetConnectionString("signalsdb")
    ?? throw new InvalidOperationException("Connection string 'signalsdb' (Kusto database) is not configured.");
builder.Services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential(new DefaultAzureCredentialOptions
{
    ManagedIdentityClientId = builder.Configuration["AZURE_CLIENT_ID"],
}));
builder.Services.AddSingleton<IKustoGateway>(sp => new KustoGateway(
    kustoConnectionString, sp.GetRequiredService<TokenCredential>(), sp.GetRequiredService<ILogger<KustoGateway>>()));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ISignalArchive>(sp => new BlobSignalArchive(sp.GetRequiredService<BlobContainerClient>()));
builder.Services.AddSingleton<IngestionQueue>();
builder.Services.AddSingleton<SignalIntake>();
builder.Services.AddSingleton<SignalInsights>();

builder.Services.AddSingleton<KustoSchemaInitializer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<KustoSchemaInitializer>());
builder.Services.AddHealthChecks().AddCheck<KustoSchemaInitializer>("kusto-schema");
builder.Services.AddSingleton<KustoIngestionWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<KustoIngestionWorker>());
builder.Services.AddHostedService<ArchiveReconciler>();

builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<SignalTools>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapDefaultEndpoints();
app.MapSignalEndpoints();
app.MapMcp("/mcp").RequireAuthorization(SignalPolicies.Any).RequireRateLimiting("signals");

app.Run();

public partial class Program;
