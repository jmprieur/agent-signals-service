#Requires -Version 7
<#
.SYNOPSIS
Sends one Agent Signal (a JSON file) to an Agent Signals service, or keeps it locally if the
service cannot be reached, so signal capture never blocks the developer.

.EXAMPLE
# Local run (aspire run): no token needed.
./samples/Send-Signal.ps1 -ApiUrl http://localhost:5280 -Path ./samples/signals/execution.json -Local

.EXAMPLE
# Deployed service: uses your Azure CLI sign-in. You need the Signals.Write app role.
./samples/Send-Signal.ps1 -ApiUrl https://api.<your-environment>.azurecontainerapps.io -ApiClientId <client id> -Path ./my-signal.json
#>
[CmdletBinding(DefaultParameterSetName = 'Deployed')]
param(
    [Parameter(Mandatory)] [uri] $ApiUrl,
    [Parameter(Mandatory)] [string] $Path,
    [Parameter(Mandatory, ParameterSetName = 'Deployed')] [guid] $ApiClientId,
    [Parameter(Mandatory, ParameterSetName = 'Local')] [switch] $Local
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$headers = @{}
if (-not $Local) {
    $token = az account get-access-token --scope "api://$ApiClientId/Signals.Access" --query accessToken -o tsv --only-show-errors
    if ($LASTEXITCODE -ne 0 -or -not $token) { throw 'Could not get a token from the Azure CLI. Run az login first.' }
    $headers.Authorization = "Bearer $token"
}

try {
    $result = Invoke-RestMethod -Method Post -Uri ([uri]::new($ApiUrl, '/v1/signals')) -Headers $headers `
        -ContentType 'application/json' -InFile $Path -SkipHttpErrorCheck -StatusCodeVariable status
    if ($status -in 200, 202) {
        $result | ConvertTo-Json -Depth 5
        return
    }

    Write-Warning "The service answered HTTP $status."
    $result | ConvertTo-Json -Depth 5
    if ($status -lt 500 -and $status -ne 429) { return }
}
catch {
    Write-Warning "Could not reach the service: $($_.Exception.Message)"
}

# The protocol's fallback: never lose a signal because the service is unavailable.
New-Item -ItemType Directory -Force -Path .signals | Out-Null
$copy = Join-Path .signals ("{0:yyyyMMddTHHmmss}-{1}" -f (Get-Date).ToUniversalTime(), (Split-Path $Path -Leaf))
Copy-Item -Path $Path -Destination $copy
Write-Warning "Kept the signal locally at $copy. Send it again later."
