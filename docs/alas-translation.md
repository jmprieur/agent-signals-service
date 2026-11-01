# Accepting ALAS signals

ALAS agents already emit signals, but in their own "1.0" format, which predates the
[Agent Signals protocol](https://github.com/jennyf19/agentic-devops/tree/main/agent-signals).
This service accepts that format and converts it to protocol 0.1.1 when a signal arrives, so ALAS
agents can switch to this service without changing what they send.

Every converted signal is marked `source_format: "alas/1.0"` and keeps its original values under
`translation.original`, so anyone can check a conversion and the mapping can be changed later.

## How a signal is recognized as ALAS

A signal is treated as ALAS when its `schema_version` is `1` or `1.x` (string or number), or when its
`signal_type` is `human-feedback` (ALAS human-feedback signals carry no `schema_version`).

## What changes

| ALAS field | Becomes | Rule |
| --- | --- | --- |
| `schema_version` `1.0` | `0.1.1` | Original kept |
| outcome `quality_rating` `A` / `B` / `C` | `5` / `3` / `1` | Matches the protocol's descriptions: 5 excellent, 3 adequate with known limitations, 1 poor. ALAS defines the grades only as green, orange and red. Original kept. |
| outcome `effort_to_merge` `Low` / `Medium` / `High` | `minimal` / `moderate` / `significant` | Original kept |
| execution `mode` `automated`, `service` | `autonomous` | Original kept |
| `kpi.{id, name, program}`, top-level `kpi_id`, `kpi_name`, `program`, outcome `skill_context.kpi_*` | `labels.kpi_id`, `labels.kpi_name`, `labels.program` | Free-form labels |
| outcome `skill_context.skill_used` | `skill_used` | When `skill_used` is absent |
| outcome `context_level` (`full` / `limited`) | `evidence_level` | Typed extension |
| outcome `issues_found` as objects (`issue` or `description`, `severity`, `risk`) | `issues_found` as text | The full objects are kept in `translation.original` |
| execution `execution_attribution` | `attribution` | Model and reasoning effort |
| execution `outcome.{changes_applied, pr_created, turns_taken}` | `result.*` | The rest of that object stays as `alas_outcome` (not scored) |
| execution `dev_feedback` | `human_feedback` | "Dev" is the agent: this is the human's answer to a question the agent asked about its run. `question_scope` → `scope`, `arm` → `variant`. Optional: autonomous runs omit it. |
| standalone `human-feedback` signal | a human-feedback record for the run | Stored but not a protocol signal type, so marked non-conforming |
| self-assessment score sent as text (`"3"`) or as `{ "score": 4, ... }` | the integer | Noted in `translation.notes` |
| pattern sent as a list | one text value joined with `; ` | |
| missing `timestamp` (74% of ALAS outcomes) | the time the service received the signal | Noted in `translation.notes` |

**Removed before storage:** `pr_url` (in the execution's `outcome` object and at the top level of
outcomes). The protocol's privacy rules forbid repository URLs.

**Not used in any score**, but kept as received: `outcome.escalation_needed` (true in 69% of ALAS
runs, so it does not mean what the protocol's escalation means), `developer_accepted`,
`developer_modified`, `changes_proposed`, `escape_hatch_used`, `full_journey`, `evaluator`,
`skill_provenance`, `migration_context`, `notes`.

ALAS sometimes sent the same human rating twice: once inside the execution signal and once as a
standalone `human-feedback` signal. In the 99 runs where both exist, the ratings were identical in
every case. Reports keep one rating per run and scope.

## Results on real ALAS signals

On 2026-10-29, the translator and validator were run (in memory only, nothing stored) over signals
from the ALAS production Kusto database: a random sample of 3,000 executions and every outcome and
human-feedback signal.

| Signal type | Signals | Conforming after translation | Main reasons for the rest |
| --- | --- | --- | --- |
| execution | 3,000 (sample) | 96.8% | 66 `run_id` values that are not UUIDs; 23 with no self-assessment; 1 rejected for private data |
| outcome | 2,200 (all) | 98.8% | 18 with no quality rating; 6 non-UUID `run_id`; 1 rejected for private data |
| human-feedback | 192 (all) | not a protocol type | all 192 ratings recovered as human feedback |

Non-conforming signals are still stored with their issues; they are only left out of the scores.
