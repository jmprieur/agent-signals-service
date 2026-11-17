# Sending signals

This page is for people who build or run agents, and for whoever grades their work. In short:

1. **After each run**, the agent that did the work sends an **execution** signal: its own view of how
   the run went.
2. **When someone can judge the result**, they send an **outcome** signal with the same `run_id`.
   Many runs never get one, and that is fine.
3. **When the agent needs a person**, it sends an **escalation** signal.

The words and every field are defined in the [signals glossary](glossary.md).

## The smallest useful signal

An execution signal needs six common fields and three scores. A skill name and one pattern make it
far more useful:

```json
{
  "signal_type": "execution",
  "schema_version": "0.1.1",
  "run_id": "3f6d2a0e-8c41-4b7e-9a55-1f2e7c9d0b13",
  "timestamp": "2026-11-17T18:00:00Z",
  "agent_name": "build-fixer",
  "skill_used": "fix-failing-build",
  "self_assessment": { "confidence": 4, "accuracy": 4, "completeness": 5 },
  "patterns": { "tsg_gap": "no rollback steps for multi-module projects" }
}
```

- `run_id`: a new UUID for each run. Reuse it for any outcome or escalation about the same run.
- `skill_used`: the kind of task. Use the same names every time; reports group by them.
- Scores are whole numbers from 1 to 5; see [Writing good signals](#writing-good-signals).

The protocol's [full examples](../samples/signals) show every field.

## How to send

Most signals come from agents, so the main way in is the service's MCP server. Use HTTP for scripts,
pipelines and automated evaluators.

### From an agent, through MCP

The service is an MCP server at `/mcp` (for example `http://localhost:5280/mcp` locally). An agent
that has it registered calls the `submit_signal` tool with the signal as a JSON object, and reads
reports with the other tools. The tool answers with the same result as HTTP (see the table below).
[Running locally](local-development.md#with-an-agent-through-mcp) shows how to register it in VS Code.
Submitting through MCP has not been tested yet; reading reports has.

The agent also needs to know *when* to send and *how* to grade itself. Put that in the agent's own
instructions (for example `AGENTS.md`), not in each prompt. For example:

> After each task, call `submit_signal` with an execution signal (Agent Signals 0.1.1). Use a new
> UUID as `run_id` and the task type as `skill_used`. Score `confidence`, `accuracy` and
> `completeness` from 1 to 5: 1 poor, 2 notable gaps, 3 adequate with known limitations, 4 good with
> minor gaps, 5 excellent. `confidence` is how sure you are the result will hold up when someone
> checks it. In `patterns`, name what was missing in a few words; leave a field out if nothing
> applies. Never include code, names, email addresses, URLs or secrets. If the tool is unavailable,
> save the signal as a JSON file in `.signals/` instead.

### Over HTTP

`POST /v1/signals` with a JSON body: one signal, or an array of up to 100.

```powershell
Invoke-RestMethod -Method Post -Uri http://localhost:5280/v1/signals -ContentType application/json -InFile ./my-signal.json
```

[`samples/Send-Signal.ps1`](../samples/Send-Signal.ps1) does this, gets a token when the service is
deployed, and keeps the signal locally if the service cannot be reached.

| Answer | Meaning | What to do |
| --- | --- | --- |
| 202, `status: accepted` | Stored. It reaches the reports a few seconds later | Nothing |
| 202, `status: duplicate` | The same signal was already stored | Nothing; retrying is safe |
| 202 with `conforming: false` | Stored, but it breaks a field rule, so it is left out of the scores. `issues` says why | Fix the sender |
| 422, `status: rejected` | **Not stored**: it looked like it contained private data (an email address, a secret), was larger than 64 KB, or was not a JSON object. `message` says which | Fix it and send again |
| 400 | The body is not valid JSON, or the array is empty or longer than 100 | Fix the request |
| 413 | The whole request is larger than 1 MB | Send smaller arrays |
| 401 | No token, or an invalid one | See [Who can send and read signals](access.md) |
| 403 | Valid token, but the caller lacks the `Signals.Write` role | Assign the role ([how](access.md#assigning-roles)) |
| 429 | More than 600 requests a minute from one application | Slow down, or send arrays |
| 503 | Storage is unavailable | Keep the signal and retry later |

A batch (array) answers 200 with one result per signal, in order; a signal rejected inside a batch
does not stop the others.

### Signing in

Running locally, no sign-in is needed. A deployed service needs a Microsoft Entra token and an app
role; [Who can send and read signals](access.md) explains both for agents running in a developer's
session and for agents running as an application.

### If the service is down

Never let a signal block the work. Keep it in a local `.signals/` folder and send it later; this is
the protocol's own fallback, and `Send-Signal.ps1` does it for you.

## Writing good signals

**Scores.** Every score uses the protocol's scale:

| Score | Meaning |
| --- | --- |
| 1 | Poor: significant issues, low confidence |
| 2 | Below average: notable gaps or uncertainty |
| 3 | Adequate: works, with known limitations |
| 4 | Good: solid, minor gaps |
| 5 | Excellent: comprehensive and confident |

**Confidence is a prediction**, not a mood: how likely is it that the result holds up when someone
checks it? Use the whole scale. An agent that always answers 3 looks well calibrated without telling
you anything ([why](improving-agents.md#calibration-how-far-to-trust-an-agents-confidence)).

**Patterns.** Short and specific beats long and vague. The `patterns` report compares the **whole
value** of a field (ignoring case and trailing punctuation), so "no rollback steps for multi-module
projects" reported three times is found, but three different phrasings are not. Two ways to make
values match:

- **Agree on a short list of values** for common problems (`missing-rollback-steps`,
  `access-denied`, `throttled`) and have agents use them exactly. Adding details after a code makes
  each value different again, so put details in another field, such as `what_was_hard`.
- **Or let wording vary**, and group similar values afterwards with the `pattern-evidence` report,
  by hand or with a reviewer agent.

**Privacy.** Signals describe the work, never the people. Do not include code, names, email
addresses, repository or other URLs, secrets, or customer details. The service rejects some of these
(see [Privacy](how-it-works.md#privacy)), but not all.

## Sending outcomes

An outcome is an [independent grade of the result of one run](glossary.md#outcome-signal). It can
come from anyone or anything that did not do the work:

- **A person** who reviewed the result: the reviewer of a pull request, the owner of a ticket.
- **An evaluator agent** that checks the work against criteria.
- **An automated check** that turns a later fact into a grade: tests passed or failed, a pull request
  merged unchanged or reworked, a ticket closed or reopened, a change rolled back.

```json
{
  "signal_type": "outcome",
  "schema_version": "0.1.1",
  "run_id": "3f6d2a0e-8c41-4b7e-9a55-1f2e7c9d0b13",
  "timestamp": "2026-11-18T09:00:00Z",
  "agent_name": "release-review",
  "quality_rating": 4,
  "effort_to_merge": "minimal"
}
```

Use the `run_id` of the execution you are grading, and the evaluator's own name in `agent_name`, so
evaluators can be told apart.

**Choosing a grade.** The protocol fixes the scale, not how you reach a grade; that depends on your
work. Write your mapping down and apply it consistently. For example, a team whose agent writes pull
requests might choose: merged unchanged 5, merged after small edits 4, merged after substantial
rework 2, abandoned or rejected 1.

- **Make sure the top grade is reachable.** If the best result your check can observe maps to 4, an
  agent that rightly reports confidence 5 will always look like it over-claims.
- **Grade the result, not the effort.** A run that took a long detour but produced the right result
  deserves a good grade; the detour belongs in the agent's own `what_was_hard`.
- **Grade a random sample if you cannot grade everything.** Grading only the runs that went wrong
  makes agents look worse calibrated than they are.

**Timing.** Outcomes often arrive later: after a review, after tests, after a ticket stays closed for
a week. Send them whenever they are known. Reports pair an execution with its outcome only when both
fall within the report's window, so a long delay needs a longer window.

## Sending escalations

When the agent needs a person to decide or provide something, it sends an escalation with the run's
`run_id`, a `reason` and a `severity` (`low`, `medium`, `high` or `critical`). Add `blocking: true` if
work cannot continue. Keep the reason free of private details, as for any signal.
