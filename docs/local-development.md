# Running locally

Run the whole service on your machine, with local stand-ins (emulators) for Azure Blob Storage and
Azure Data Explorer (Kusto). No Azure account is needed, and the API accepts calls from your machine
without signing in.

## Prerequisites

| Need | Check | Notes |
| --- | --- | --- |
| .NET 10 SDK | `dotnet --list-sdks` shows `10.0.1xx` or later | |
| Aspire CLI, version 17 preview | `aspire --version` starts with `17.` | Install: `iex "& { $(irm https://aspire.dev/install.ps1) } -Quality dev"` (PowerShell). An older version cannot run this project; see [Troubleshooting](#troubleshooting) |
| Docker Desktop or Podman, running | `docker info` or `podman info` | Runs the emulators. With Podman, set `ASPIRE_CONTAINER_RUNTIME=podman`. No container runtime, or an ARM64 machine? See [Without the emulators](#without-the-emulators) |
| PowerShell 7 | `pwsh --version` | Only for the sample scripts; works on Windows, macOS and Linux |

If your network blocks nuget.org, first follow [Restoring packages on a restricted network](#restoring-packages-on-a-restricted-network).

## Run

```bash
aspire run --apphost src/AgentSignals.AppHost
```

It prints the address of the Aspire dashboard, with a sign-in token; the dashboard shows each part of
the service and its logs. The first run downloads the emulator images; after that, the service is
ready in about a minute. Check with `http://localhost:5280/health`, which answers `Healthy`.

## Try it

### With an agent, through MCP

Agent Signals is meant for agents, so the main way in is the service's MCP server, at
`http://localhost:5280/mcp`. To use it from an agent in VS Code, register it in the workspace's
`.vscode/mcp.json`:

```json
{
  "servers": {
    "agent-signals": { "type": "http", "url": "http://localhost:5280/mcp" }
  }
}
```

Then ask the agent, for example: "Use the agent-signals tools to show me the overview for the last
day." The tools are `submit_signal` to send a signal and `get_overview`, `get_skill_health` and others
to read reports ([full list](improving-agents.md#the-reports-at-a-glance)). Reading reports through
MCP has been tested; submitting signals through MCP has not yet.

The agent is offered every tool of a registered MCP server in every chat of that workspace, so
register it only in workspaces where you want agents to use it. To make an agent send a signal after
each task, see [Sending signals](sending-signals.md#from-an-agent-through-mcp).

### From the command line, over HTTP

Send the protocol's example execution and the outcome that grades it, as one new run timestamped now,
then read a report:

```powershell
$run = [guid]::NewGuid()
./samples/Send-Signal.ps1 -ApiUrl http://localhost:5280 -Path ./samples/signals/execution.json -Local -RunId $run -Now
./samples/Send-Signal.ps1 -ApiUrl http://localhost:5280 -Path ./samples/signals/outcome.json -Local -RunId $run -Now
Invoke-RestMethod "http://localhost:5280/v1/insights/overview?days=1"
```

Each signal should come back with `status: accepted` and `conforming: true`. Accepted means stored;
the signal reaches the reports a few seconds later, so if the counts are zero, ask again. `-RunId`
and `-Now` matter: the examples carry a fixed past date that can fall outside every report window,
and an outcome only counts against the execution with the same `run_id`.

## Stop

Press Ctrl+C in the `aspire run` window, or run `aspire stop`.

## Troubleshooting

| Symptom | Cause | Fix |
| --- | --- | --- |
| Build stops with `ASPIRE009` (CLI bundle not found), `aspire run` hangs on "Connecting to apphost…", or `aspire deploy` says "The AppHost is in publish mode" | Aspire CLI older than 17 | Install the 17 preview (see [Prerequisites](#prerequisites)) |
| Restore fails for every package | nuget.org is blocked on your network | [Restoring packages on a restricted network](#restoring-packages-on-a-restricted-network) |
| "Developer certificates are not trusted" | Local HTTPS certificate not trusted yet | Harmless; `aspire certs trust` removes it |
| Emulator containers still running after the service stopped | The process was killed instead of stopped | `docker ps`, then `docker stop <id>` for each |
| The Kusto emulator does not start on an ARM64 machine | Its image is published for x64 only | [Without the emulators](#without-the-emulators) |

## Without the emulators

If the emulators can't run on your machine, point the local run at real Azure resources instead.
Each is independent: replace only Kusto, only storage, or both.

| Situation | What to do |
| --- | --- |
| No container runtime | Use existing resources for both storage and Kusto |
| Windows or macOS on ARM64 | The Kusto emulator is x64 only: use an existing Kusto database. Azurite (the storage emulator) runs on ARM64 |
| x64 machine with a container runtime | Nothing to do; the emulators work |

This only affects local runs. Deploying always creates its own resources.

### Tell the service which resources to use

Set a connection string in the AppHost's user secrets (kept outside the repository). When one is
set, the service uses it instead of starting that emulator:

```powershell
dotnet user-secrets set "ConnectionStrings:signalsdb" "Data Source=https://<cluster>.kusto.windows.net;Initial Catalog=<database>" --project src/AgentSignals.AppHost
dotnet user-secrets set "ConnectionStrings:signals" "Endpoint=https://<account>.blob.core.windows.net/;ContainerName=signals" --project src/AgentSignals.AppHost
```

| Connection string | Replaces |
| --- | --- |
| `ConnectionStrings:signalsdb` | The Kusto emulator |
| `ConnectionStrings:signals` | The Azurite storage emulator |

Remove a secret (`dotnet user-secrets remove "ConnectionStrings:signalsdb" --project src/AgentSignals.AppHost`)
to go back to the emulator.

Neither connection string contains a key. The service signs in to both resources as **you**, with
your Azure CLI sign-in (or Visual Studio's), so run `az login` with the right account before
`aspire run`.

### A Kusto database for free

Azure Data Explorer offers a [free cluster](https://dataexplorer.azure.com/freecluster) that needs no
Azure subscription, only a Microsoft account or work account. It has limited capacity and no service
level agreement, which is fine for development.

1. Create the free cluster and a database in it (for example `signals`).
2. Copy the cluster URI from the cluster's overview page.
3. Set `ConnectionStrings:signalsdb` to `Data Source=<cluster URI>;Initial Catalog=<database>`.

At startup the service creates its tables and functions in that database, so your account must be an
administrator of it. The free cluster makes its creator an administrator. On an existing cluster in
your subscription, ask for the **Database Admin** role on the database you use.

### An existing storage account

1. Give yourself the **Storage Blob Data Contributor** role on the storage account.
2. Create the container once:

   ```powershell
   az storage container create --account-name <account> --name signals --auth-mode login
   ```

3. Set `ConnectionStrings:signals` to `Endpoint=https://<account>.blob.core.windows.net/;ContainerName=signals`.

Use a development account: every signal you send locally is stored there.

## Restoring packages on a restricted network

Some networks block nuget.org and provide a package mirror instead. The repository's `NuGet.config`
uses nuget.org plus the Aspire daily feed (the Aspire 17 preview packages are not on nuget.org yet),
so on such a network the restore fails. Your mirror may not carry the preview packages either.

Keep a config file **outside the repository** that uses your mirror for everything except Aspire:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="mirror" value="https://<your package mirror>/v3/index.json" />
    <add key="aspire-daily" value="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet9/nuget/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="aspire-daily"><package pattern="Aspire.*" /></packageSource>
    <packageSource key="mirror"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
```

Restore with it once, then build and run as usual:

```powershell
dotnet restore AgentSignals.slnx --configfile <path>\nuget-mirror.config
dotnet build AgentSignals.slnx --no-restore
```

Keep the `packageSourceMapping` section: with two sources and central package management, NuGet
otherwise stops with error NU1507. If your network also blocks the Aspire daily feed, ask whoever
runs your mirror to add it as an upstream source.
