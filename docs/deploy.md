# Deploying to your Azure subscription

One command creates everything the service needs in your subscription. Afterwards, you decide who
may send and read signals.

> **Not yet tested end to end.** The deployment templates generate correctly, but the service has not
> yet been deployed. Expect to be among the first; please report what you find.

## Before you start

| Need | Why |
| --- | --- |
| The [local prerequisites](local-development.md#prerequisites): .NET 10 SDK and Aspire CLI 17 preview | To build and deploy |
| Docker Desktop or Podman, **running** | Aspire builds the service's container image on your machine and uploads it. Needed even if you never run the emulators. The image is always built for x64, which is what Azure Container Apps runs, also when you deploy from an ARM64 machine |
| An Azure subscription where you can create resources | For the resources below |
| Permission to create app registrations in that subscription's Microsoft Entra tenant | The service gets its own app registration. Some organizations restrict this; use a tenant where you are allowed to |
| `az login` with that account | Aspire deploys with your Azure CLI sign-in |

If your Azure CLI is signed in to several accounts, you can keep a separate sign-in for deploying in
one terminal only:

```powershell
$env:AZURE_CONFIG_DIR = "$HOME\.azure-deploy"   # a separate Azure CLI profile, for this terminal only
az login --tenant <tenant id>
$env:Azure__CredentialSource = "AzureCli"       # make Aspire use this sign-in, not another saved one
```

## Deploy

```bash
aspire deploy --apphost src/AgentSignals.AppHost
```

Aspire asks for a tenant, a subscription, a resource group and a region. Choose a specific region such
as `westus2` or `australiaeast`, not a geography such as "Australia". To answer the questions again on
a later run, add `--clear-cache`.

It creates:

| Resource | Purpose |
| --- | --- |
| Azure Container Apps environment, container registry, managed identity | Runs the service, with no stored secrets |
| Storage account with a `signals` container | The archive: every signal, kept permanently. The service's identity can only read and write blobs |
| Azure Data Explorer (Kusto) cluster and `signals` database | Where reports are computed. **Dev/Test size** (one node, no SLA, stops when idle); the main running cost. Change the size in [`AppHost.cs`](../src/AgentSignals.AppHost/AppHost.cs) for production use |
| Microsoft Entra app registration `agent-signals` | Defines who may call the service: app roles `Signals.Write` and `Signals.Read`, and a `Signals.Access` scope for agents running as a signed-in developer |

## After deploying

1. **Give people and agents access.** Nobody can send or read signals until you assign app roles;
   until then, a caller with a valid token gets HTTP 403 (a caller without a token always gets 401).
   See [Who can send and read signals](access.md).
2. **Send a first signal** with your own sign-in. You need the `Signals.Write` role. You also need the
   service's address (the URL of the `api` container app, in the Azure portal) and the application
   (client) ID of the `agent-signals` app registration (Microsoft Entra ID, App registrations):

   ```powershell
   ./samples/Send-Signal.ps1 -ApiUrl https://<service address> -ApiClientId <agent-signals client id> -Path ./samples/signals/execution.json -RunId ([guid]::NewGuid()) -Now
   ```

3. **Connect your agents.** See [Sending signals](sending-signals.md).
4. **To query Kusto yourself**, give your account the Viewer role on the database. See
   [Querying Kusto directly](improving-agents.md#querying-kusto-directly).

## Troubleshooting

| Symptom | Cause | Fix |
| --- | --- | --- |
| `LocationNotAvailableForResourceGroup` | A geography was chosen instead of a region | Deploy again with `--clear-cache` and choose a region such as `australiaeast` |
| "Docker is not running" or "No container runtime detected" | No container runtime running | Start Docker Desktop or Podman (with `ASPIRE_CONTAINER_RUNTIME=podman`) |
| "The AppHost is in publish mode and cannot be started with 'aspire run'" | Aspire CLI older than 17 | Install the 17 preview ([prerequisites](local-development.md#prerequisites)) |
| Failure creating the app registration | Your account cannot create app registrations in this tenant | Use a tenant where you can, or ask an administrator |
