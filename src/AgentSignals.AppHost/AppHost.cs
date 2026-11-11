using Aspire.Hosting.Azure;
using Aspire.Hosting.Publishing;
using Azure.Provisioning.Kusto;
using Azure.Provisioning.Storage;
using Microsoft.Extensions.Configuration;

// Every Azure resource the service needs is declared here. `aspire run` uses local emulators;
// `aspire deploy` creates the same resources in your own subscription.
var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddDotnetProject("api", "../AgentSignals.Api/AgentSignals.Api.csproj")
    .WithHttpHealthCheck("/health");

// Local runs can use an existing resource instead of an emulator by setting its connection string
// (ConnectionStrings:signals, ConnectionStrings:signalsdb) in the AppHost's user secrets. This is how to
// run on machines that cannot run the emulator containers, for example Windows on ARM64: the Kusto
// emulator image is x64-only. See docs/local-development.md. Deployment always creates its own resources.
bool UseExisting(string name) =>
    !builder.ExecutionContext.IsPublishMode && !string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString(name));

// Durable archive: one immutable JSON file per accepted signal.
if (UseExisting("signals"))
{
    api.WithReference(builder.AddConnectionString("signals"));
}
else
{
    var storage = builder.AddAzureStorage("storage").RunAsEmulator();
    var signalsContainer = storage.AddBlobContainer("signals");
    api.WithReference(signalsContainer).WaitFor(signalsContainer)
        .WithRoleAssignments(storage, StorageBuiltInRole.StorageBlobDataContributor);
}

// Queryable store: typed tables and the analysis functions (created by the API at startup).
if (UseExisting("signalsdb"))
{
    api.WithReference(builder.AddConnectionString("signalsdb"));
}
else
{
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
    api.WithReference(signalsDb).WaitFor(signalsDb);
}

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

    // Container Apps runs x64 images. Without this, deploying from an ARM64 machine builds an ARM64 image.
#pragma warning disable ASPIREPIPELINES003 // Container build options are experimental.
    api.WithContainerBuildOptions(options => options.TargetPlatform = ContainerTargetPlatform.LinuxAmd64);
#pragma warning restore ASPIREPIPELINES003
}
else
{
    // Local runs accept loopback callers without tokens. The API refuses this mode outside Development.
    api.WithEnvironment("Auth__Mode", "LocalDevelopment");
}

builder.Build().Run();
