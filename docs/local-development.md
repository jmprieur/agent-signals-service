# Running locally

`aspire run` normally starts two local emulators as containers: Azurite for Blob Storage and the
Kusto emulator for Azure Data Explorer. That needs a container runtime (Docker Desktop, or Podman
with `ASPIRE_CONTAINER_RUNTIME=podman`).

If the emulators can't run on your machine, point the local run at real resources instead. Each
one is independent: you can replace only Kusto, only storage, or both.

| Situation | What to do |
| --- | --- |
| No container runtime | Use existing resources for both storage and Kusto |
| **Windows or macOS on ARM64** | The Kusto emulator image is published for x64 only. Use an existing Kusto database. Azurite runs on ARM64, so storage can stay local if you have a container runtime |
| x64 machine with a container runtime | Nothing to do; the emulators work |

Deployment (`aspire deploy`) is not affected: it always creates its own resources.

## How it works

The AppHost looks for two connection strings in its own configuration. When one is set, it passes
it to the API instead of starting that emulator:

| Connection string | Replaces |
| --- | --- |
| `ConnectionStrings:signalsdb` | The Kusto emulator |
| `ConnectionStrings:signals` | The Azurite storage emulator |

Store them in the AppHost's user secrets, so they stay out of the repository:

```powershell
dotnet user-secrets set "ConnectionStrings:signalsdb" "Data Source=https://<cluster>.kusto.windows.net;Initial Catalog=<database>" --project src/AgentSignals.AppHost
dotnet user-secrets set "ConnectionStrings:signals" "Endpoint=https://<account>.blob.core.windows.net/;ContainerName=signals" --project src/AgentSignals.AppHost
```

Remove a secret (`dotnet user-secrets remove "ConnectionStrings:signalsdb" --project src/AgentSignals.AppHost`)
to go back to the emulator.

## Signing in

Neither connection string contains a key. The API signs in to both resources as **you**, using your
Azure CLI sign-in (or Visual Studio's), so run `az login` with the account that has access before
`aspire run`.

## A Kusto database for free

Azure Data Explorer offers a [free cluster](https://dataexplorer.azure.com/freecluster) that needs no
Azure subscription, only a Microsoft account or work account. It has limited capacity and no service
level agreement, which is fine for development.

1. Create the free cluster and a database in it (for example `signals`).
2. Copy the cluster URI from the cluster's overview page.
3. Set `ConnectionStrings:signalsdb` to `Data Source=<cluster URI>;Initial Catalog=<database>`.

At startup the API creates its tables and functions in that database, so your account must be an
administrator of the database. The free cluster makes its creator an administrator. On an existing
cluster in your subscription, ask for the **Database Admin** role on the database you use.

## An existing storage account

1. Give yourself the **Storage Blob Data Contributor** role on the storage account.
2. Create the container once:

   ```powershell
   az storage container create --account-name <account> --name signals --auth-mode login
   ```

3. Set `ConnectionStrings:signals` to `Endpoint=https://<account>.blob.core.windows.net/;ContainerName=signals`.

Use a development account: every signal you send locally is stored there.
