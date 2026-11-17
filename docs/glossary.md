# Signals glossary

Every signal answers one of two questions:

- **What does the agent say about its own work?** That is an **execution** signal, sent by the agent
  that did the work, right after it finished.
- **What actually happened?** That is an **outcome** signal, sent by someone else, whenever the result
  can be judged.

Keep the two apart. An execution signal is a claim; an outcome is a check of that claim. Most of
what the reports can tell you comes from comparing them, but much of it also works with executions
alone (see [Executions without outcomes](#executions-without-outcomes)).

Field rules come from the [Agent Signals protocol](https://github.com/jennyf19/agentic-devops/blob/main/agent-signals/SIGNAL.md)
(schema 0.1.1). Fields marked *extension* are added by this service, mostly from ALAS; they are
candidates for the protocol, not part of it.

## The four signals

| Signal | Sent by | When | Represents |
| --- | --- | --- | --- |
| **Execution** | The agent that did the work | At the end of each run | The agent's own view of its work: how sure it is, what was hard, what was missing |
| **Outcome** | Anyone except that agent: a person, an evaluator agent, an automated check | When the result can be judged; often hours or days later | An independent grade of the result of that run |
| **Escalation** | The agent that is doing the work | When it needs a person | A request for human help, and how urgent it is |
| **Partnership** | A reviewer agent that reads many signals | On a schedule | Patterns it found across runs, and what it recommends |

## Terms

| Term | Meaning |
| --- | --- |
| **Run** | One piece of work by one agent: a task, a request, a case. The unit everything is counted in |
| **`run_id`** | A UUID that identifies a run. The execution and every outcome or escalation about that run carry the same `run_id`; that is how they are joined |
| **Agent** (`agent_name`) | Who sent the signal. In an execution, the working agent; in an outcome, the evaluator |
| **Skill** (`skill_used`) | The kind of task the run was: the skill, workflow or request type. Reports group by it |
| **Self-assessment** | The scores an agent gives its own work, in an execution signal |
| **Evaluator** | Whoever sends an outcome. Never the agent that did the work |
| **Paired run** | A run that has both an execution and an outcome in the report's window. Only paired runs count in calibration |
| **Calibration** | How close the agent's `confidence` is to the evaluator's `quality_rating`, on a 1 to 5 scale where 5 is a perfect match. See [calibration](improving-agents.md#calibration-how-far-to-trust-an-agents-confidence) |
| **Over-claiming / under-claiming** | Confidence above / below the evaluator's grade. Over-claiming is the risky direction; under-claiming is safer but not more honest |
| **Conforming** | The signal follows the protocol's field rules. Non-conforming signals are stored but left out of every score |
| **Window** | The number of days a report looks back over (`days`). Only signals in the window count |
| **Handoff** | Active work passed from one session or agent to another. The successor's execution sets `resumed_from_run_id` |

## Execution signal

Sent by the working agent at the end of each run. **Everything in it is the agent's own claim**,
unchecked until an outcome arrives.

**Self-assessment** (`self_assessment`), each 1 to 5, higher is better:

| Field | Required | The agent's answer to |
| --- | --- | --- |
| `confidence` | yes | How sure am I that this result is right? A prediction of how the result will be judged; the score compared with the outcome |
| `accuracy` | yes | How correct do I think the work is? |
| `completeness` | yes | How much of what was asked did I do? |
| `continuation_readiness` | no | If I am handing this work over, how safely can the next session pick it up? |
| `tsg_alignment` | no, *extension* | How well did the troubleshooting guide match what I met? |
| `developer_experience` | no, *extension* | How smoothly did the tools and environment work? |

**Patterns** (`patterns`), short text or codes. These feed the `patterns` report:

| Field | The agent's answer to |
| --- | --- |
| `what_worked` | What went well and should be kept? |
| `what_was_hard` | What was difficult? |
| `skill_gap` | What did the skill not cover? |
| `tsg_gap` | What step was missing from the troubleshooting guide or documentation? |
| `improvisation` | What did I have to make up, because no guidance existed? |
| `recurring_pattern` | What have I seen before? |
| `environment_blockers` (*extension*) | What tool, access or service got in the way? |

**Context:**

| Field | Required | Meaning |
| --- | --- | --- |
| `skill_used` | no | The kind of task. Reports group by it; `skill-health` ignores runs without it |
| `mode` | no | `interactive` (a person was present) or `autonomous` |
| `resumed_from_run_id` | no | The run this one continues from, after a handoff |
| `human_feedback` | no, *extension* | A person's answer to a question the agent asked during the run (`rating`, `scope`). Reported by the agent, so not an outcome; autonomous runs have none |
| `attribution` | no, *extension* | Which model did the work (`observed_model`); used by `model-calibration` |
| `invoked_skills` | no, *extension* | Every skill the run loaded |
| `result` | no, *extension* | Facts the agent reports about its run: `changes_applied`, `pr_created`, `turns_taken` |

## Outcome signal

Sent by an evaluator about a run someone else did. **An outcome is an independent grade of the
result of one run**: independent (never from the working agent), about the result (not the effort),
and about one run (same `run_id` as the execution). See [What an outcome is](improving-agents.md#what-an-outcome-is)
for who can send one and how to choose a grade.

| Field | Required | Meaning |
| --- | --- | --- |
| `quality_rating` | yes | How good was the result, judged independently? 1 to 5, the same scale as `confidence`. The evaluator's score, never the agent's |
| `effort_to_merge` | no | How much a person had to fix before the result could be used: `none`, `minimal`, `moderate`, `significant` |
| `issues_found` | no | Short descriptions of what was wrong |
| `evidence_level` | no, *extension* | Whether the evaluator saw the full context (`full`) or only part of it (`limited`) |

**Handoff grade** (`continuation`), only when the run handed work to a successor:

| Field | Meaning |
| --- | --- |
| `recovery_rating` (required in `continuation`) | How safely the successor could resume, 1 (could not) to 5 (resumed directly). Compared with the predecessor's `continuation_readiness` |
| `effort_to_resume` | `none`, `minimal`, `moderate`, `significant` |
| `provenance_loss` | Was the evidence trail lost? `none`, `minor`, `material` |
| `authority_loss` | Were the decision boundaries (what was allowed or agreed) lost? `none`, `minor`, `material` |
| `stale_path_revived` | Did the successor go back to an approach that had already been ruled out? Omit if not checked |

## Escalation signal

Sent by the working agent when it needs a person. It carries the same `run_id` as the run.

| Field | Required | Meaning |
| --- | --- | --- |
| `reason` | yes | Why help is needed |
| `severity` | yes | `low`, `medium`, `high`, `critical` |
| `blocking` | no | Whether work cannot continue without help |
| `requested_action` | no | What the person should decide or provide |

## Partnership signal

Sent by a reviewer agent that has read many signals about another agent. See
[the improvement loop](improving-agents.md#the-improvement-loop).

| Field | Required | Meaning |
| --- | --- | --- |
| `observed_agent` | yes | The agent being reviewed |
| `observation` | yes | `signal_count_reviewed` and `time_window`: how much evidence was read |
| `observed_skill` | no | The skill reviewed, if only one |
| `recommendations` | no | Suggested changes, each with a `description`, `evidence`, `priority` and `validation_status` |
| `validation_status` (in a recommendation) | no | `outcome_validated` if the evidence includes outcomes, `self_report_only` if it rests on executions alone. Treat the second as a lead, not a fact |

## Fields on every signal

| Field | Required | Meaning |
| --- | --- | --- |
| `signal_type` | yes | `execution`, `outcome`, `escalation` or `partnership` |
| `schema_version` | yes | `0.1.1` (or `0.1.0` without handoff fields) |
| `run_id` | yes | The run the signal is about (a UUID) |
| `timestamp` | yes | When it happened, ISO 8601 in UTC |
| `agent_name` | yes | Who sent it |
| `session_id` | no, *extension* | Groups runs from one working session |
| `labels` | no, *extension* | Free-form context, such as a program or KPI name |

## Executions without outcomes

**This is the normal case.** Many runs are never graded: nobody reviews them, the result is not
visible yet, or no check exists for that kind of task. An execution with no outcome is still useful;
it just cannot tell you whether the agent's confidence deserves trust.

| Report | Without outcomes |
| --- | --- |
| `overview` | Works. `executions_without_outcome` shows how many runs are ungraded |
| `patterns`, `pattern-evidence` | Work fully: they read executions only |
| `skill-health` | Low confidence, escalation rate and confidence decline work. Overconfidence needs outcomes |
| `escalations`, `human-feedback` | Work fully |
| `recommendations` | Work, but recommendations can only be `self_report_only` |
| `calibration`, `model-calibration` | Empty: nothing to compare confidence with |
| `continuation` | Shows readiness claims, all counted as `unverified_handoffs` |

How to read executions on their own:

- **Treat every score as a claim.** A skill whose agents report confidence 5 may be excellent or
  overconfident; executions alone cannot tell which.
- **Patterns are the most reliable part.** "The guide is missing step 4", reported by three runs, is
  useful whether or not anyone graded those runs.
- **Grade a sample if you cannot grade everything.** Outcomes for even a fraction of runs make
  calibration possible for that fraction. Pick the sample at random, not only the runs that went
  wrong, or calibration will look worse than it is.
