using Aspire.Hosting.Azure;
using Azure.Provisioning.Kusto;
using Azure.Provisioning.Storage;

// Every Azure resource the service needs is declared here. `aspire run` uses local emulators;
// `aspire deploy` creates the same resources in your own subscription.
var builder = DistributedApplication.CreateBuilder(args);

// Durable archive: one immutable JSON file per accepted signal.
var storage = builder.AddAzureStorage("storage").RunAsEmulator();
var signalsContainer = storage.AddBlobContainer("signals");

// Queryable store: typed tables and the analysis functions (created by the API at startup).
// Dev/Test size by default (one node, no SLA, stops when idle). Change it here for production volume.
var kusto = builder.AddAzureKustoCluster("kusto")
    .RunAsEmulator()
    .ConfigureInfrastructure(infra =>
    {
#pragma warning disable AZPROVISION001 // Azure.Provisioning.Kusto is in preview.
        var cluster = infra.GetProvisionableResources().OfType<KustoCluster>().Single();
        cluster.Sku = new KustoSku
        {
            Name = KustoSkuName.DevNoSlaStandardE2aV4,
            Tier = KustoSkuTier.Basic,
            Capacity = 1,
        };
        cluster.IsAutoStopEnabled = true;
#pragma warning restore AZPROVISION001
    });
var signalsDb = kusto.AddReadWriteDatabase("signalsdb", "signals");

var api = builder.AddDotnetProject("api", "../AgentSignals.Api/AgentSignals.Api.csproj")
    .WithReference(signalsContainer).WaitFor(signalsContainer)
    .WithReference(signalsDb).WaitFor(signalsDb)
    .WithRoleAssignments(storage, StorageBuiltInRole.StorageBlobDataContributor)
    .WithHttpHealthCheck("/health");

if (builder.ExecutionContext.IsPublishMode)
{
    builder.AddAzureContainerAppEnvironment("env");

    // The API's Entra ID registration. Agents (applications) and people are granted these app roles
    // in Microsoft Entra; Aspire declares the roles but does not assign them (see docs/access.md).
    var entra = builder.AddEntraIdApplication("agent-signals")
        .WithApplicationIdUri()
        .WithAppRole(new EntraIdAppRole("Signals.Write", "Write agent signals", "Submit agent signals.", ["Application", "User"]))
        .WithAppRole(new EntraIdAppRole("Signals.Read", "Read agent signals", "Read agent signal reports.", ["Application", "User"]))
        .WithScope("Signals.Access", "Lets an agent running in your session send and read signals as you, within your assigned app roles.");

    // Agents running in a developer's session sign in with the Azure CLI. Pre-authorizing it only removes
    // the consent prompt: the API still rejects callers without an assigned app role.
    var azureCli = builder.AddExistingEntraApplication(
        "azure-cli",
        tenantId: "f8cdef31-a31e-4b4a-93e4-5f571e91255a", // Azure CLI's home tenant (Microsoft Services)
        clientId: "04b07795-8ddb-461a-bbee-02f9e1bf7b46"); // Azure CLI's well-known application ID
    entra.WithPreAuthorizedApplication(azureCli, "Signals.Access");

    api.WithReference(entra).WithExternalHttpEndpoints();
}
else
{
    // Local runs accept loopback callers without tokens. The API refuses this mode outside Development.
    api.WithEnvironment("Auth__Mode", "LocalDevelopment");
}

builder.Build().Run();
