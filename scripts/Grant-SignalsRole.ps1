#Requires -Version 7
<#
.SYNOPSIS
Gives a user, group, or service principal (for example an agent's managed identity) an
Agent Signals app role: Signals.Write to send signals, Signals.Read to read reports.

.DESCRIPTION
Aspire declares the app roles when it creates the "agent-signals" registration, but it does not
assign them. Run this once per identity. The script is idempotent: it does nothing when the
assignment already exists, supports -WhatIf, and reads the assignment back after creating it.

Requires an Azure CLI sign-in that is allowed to assign app roles in the tenant (an owner of the
agent-signals enterprise application for users and groups; Privileged Role Administrator or
AppRoleAssignment.ReadWrite.All for service principals).

.EXAMPLE
./scripts/Grant-SignalsRole.ps1 -ApiClientId <client id> -PrincipalObjectId (az ad signed-in-user show --query id -o tsv) -Role Signals.Write -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    # Client ID of the agent-signals registration (shown in the deployment outputs).
    [Parameter(Mandatory)] [guid] $ApiClientId,

    # Object ID of the user, group, or service principal that receives the role.
    [Parameter(Mandatory)] [guid] $PrincipalObjectId,

    [Parameter(Mandatory)] [ValidateSet('Signals.Write', 'Signals.Read')] [string] $Role
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-AzJson {
    param([Parameter(Mandatory)] [string[]] $Arguments)
    $output = az @Arguments --only-show-errors -o json
    if ($LASTEXITCODE -ne 0) { throw "az $($Arguments[0..1] -join ' ') failed with exit code $LASTEXITCODE." }
    if ($output) { $output | ConvertFrom-Json }
}

$api = Invoke-AzJson @('ad', 'sp', 'show', '--id', "$ApiClientId")
$appRole = @($api.appRoles | Where-Object { $_.value -eq $Role -and $_.isEnabled })
if ($appRole.Count -ne 1) { throw "The registration $ApiClientId does not declare an enabled '$Role' app role." }

$assignedToUri = "https://graph.microsoft.com/v1.0/servicePrincipals/$($api.id)/appRoleAssignedTo"

function Get-ExistingAssignment {
    $uri = "$assignedToUri`?`$top=999"
    while ($uri) {
        $page = Invoke-AzJson @('rest', '--method', 'get', '--uri', $uri)
        $match = $page.value | Where-Object { $_.principalId -eq "$PrincipalObjectId" -and $_.appRoleId -eq $appRole[0].id }
        if ($match) { return $match }
        $uri = $page.PSObject.Properties['@odata.nextLink']?.Value
    }
}

if (Get-ExistingAssignment) {
    Write-Host "$PrincipalObjectId already has $Role on $($api.displayName). Nothing to do."
    return
}

if ($PSCmdlet.ShouldProcess("$PrincipalObjectId", "Assign $Role on $($api.displayName)")) {
    $body = @{ principalId = "$PrincipalObjectId"; resourceId = $api.id; appRoleId = $appRole[0].id } | ConvertTo-Json -Compress
    $bodyFile = New-TemporaryFile
    try {
        Set-Content -Path $bodyFile -Value $body -Encoding utf8NoBOM
        Invoke-AzJson @('rest', '--method', 'post', '--uri', $assignedToUri, '--headers', 'Content-Type=application/json', '--body', "@$bodyFile") | Out-Null
    }
    finally {
        Remove-Item $bodyFile -ErrorAction SilentlyContinue
    }

    if (-not (Get-ExistingAssignment)) { throw "The assignment was created but could not be read back. Check the enterprise application in Microsoft Entra." }
    Write-Host "Assigned $Role on $($api.displayName) to $PrincipalObjectId."
}
