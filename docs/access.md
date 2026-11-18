# Who can send and read signals

**In short:** after deploying, nobody can use the service until you give them a role. Give agents and
pipelines that send signals `Signals.Write`, and people or agents that read reports `Signals.Read`,
with [`scripts/Grant-SignalsRole.ps1`](#assigning-roles). Running locally needs none of this.

The service accepts only Microsoft Entra ID tokens for its own app registration, `agent-signals`,
which `aspire deploy` creates. Being able to get a token is not enough: the token must carry one of
the registration's app roles.

| App role | Allows |
| --- | --- |
| `Signals.Write` | The MCP `submit_signal` tool and `POST /v1/signals` |
| `Signals.Read` | The MCP report tools and `GET /v1/insights/*` |

Aspire declares these roles but does not assign them. Until you assign a role, calls without a token
are rejected with HTTP 401, and calls with a valid token but no role with HTTP 403.

## Agents running in a developer's session

Most agents, including ALAS agents in Copilot sessions, run as the signed-in developer. The
registration exposes a delegated scope, `Signals.Access`, and pre-authorizes the Azure CLI for it,
so an agent can get a token from the developer's Azure CLI sign-in without a consent prompt:

```powershell
az account get-access-token --scope "api://<client id>/Signals.Access" --query accessToken -o tsv
```

Pre-authorizing the Azure CLI only removes the consent prompt. The API still requires the developer
(or a group they belong to) to hold `Signals.Write` or `Signals.Read`.

## Agents running as an application

Pipelines and hosted agents should use a managed identity or another service principal. Assign the
role to its service principal, then request an application token for `api://<client id>/.default`.

## Assigning roles

Use [`scripts/Grant-SignalsRole.ps1`](../scripts/Grant-SignalsRole.ps1). It is idempotent, supports
`-WhatIf`, and reads the assignment back:

```powershell
# Yourself, so agents in your session can send signals
./scripts/Grant-SignalsRole.ps1 -ApiClientId <client id> -Role Signals.Write `
    -PrincipalObjectId (az ad signed-in-user show --query id -o tsv)

# A managed identity used by a pipeline
./scripts/Grant-SignalsRole.ps1 -ApiClientId <client id> -Role Signals.Write `
    -PrincipalObjectId <service principal object id>
```

Assigning roles needs an account allowed to do so in the tenant: an owner of the `agent-signals`
enterprise application can assign roles to users and groups; assigning roles to service principals
needs Privileged Role Administrator or the `AppRoleAssignment.ReadWrite.All` permission. For many
people, assign the role to a group once instead of to each person.

## What the service records about the caller

Only the calling application's ID (`azp` or `appid` claim), stored as `submitter`. The service never
stores the signed-in person's identity: the protocol forbids developer names in signals.

## Local runs

`aspire run` starts the API in a local-development mode that accepts calls from this machine only,
without tokens. The API refuses to start in that mode outside the Development environment, and it
refuses to start without Entra ID settings in every other case.
