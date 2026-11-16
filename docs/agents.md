# Multi-agent system (Week 3)

This note explains the agent concepts in RepoMind and the reasons behind each
design choice. The agents are plain C# services; the project uses no agent
framework.

## What an "agent" is here

An agent is a component with one responsibility, a small set of allowed actions
(tools), and a defined input and output. RepoMind has four:

| Agent | Responsibility | Uses a model for |
|---|---|---|
| **Supervisor** (`AgentSupervisor` + `AgentRouter`) | Classify the task, write the plan, hand work between agents, enforce the deadline, set the final status, persist the trace | nothing: routing uses rules |
| **Retrieval Agent** (`RetrievalAgent`) | Find evidence: run the verified RAG flow for location questions; for analysis/review, search, choose files, and read bounded windows | choosing which files to read (with a rule fallback) |
| **Code Analysis Agent** (`CodeAnalysisAgent`) | Explain what code does: outline the file, then write line-cited findings | explanation |
| **Review Agent** (`ReviewAgent`) | Find problems: deterministic review rules, then line-cited model findings with severity | review |

## One run, step by step

```
POST /api/repositories/38/agents/run { "task": "Review src/router/guards.ts for security issues" }

supervisor.list_files     → 12 indexed files              (tool)
supervisor.route          → review, target guards.ts      (rules)
retrieval.read_file       → E1 guards.ts:1-16             (tool)
supervisor.handoff        → retrieval → review: E1        (shared state)
review.static_checks      → no rule matches               (tool)
review.synthesize         → model findings, validated     (LLM, structured JSON)
supervisor.persist_run    → agent_runs row                (database)
```

The response contains `route`, `status`, `summary`, `findings` (each with
`citations`), `evidence`, `plan` and `trace`. The UI shows the plan and the
trace table under each result.

## Key concepts

**Routing (Supervisor).** `AgentRouter` uses regular expressions:

- side-effect verbs at the start, such as *delete, deploy, refactor, write* → `unsupported`
- review words → `review`
- explanation words → `analysis`
- anything else → `question`

A 4B local model would add 15–30 seconds to every task just to choose a route,
and could choose it inconsistently. Rules are instant, testable, and the route
always comes with a readable reason. File names in the task (`guards.ts`) are
resolved to indexed paths only when exactly one file matches.

**Planning and task decomposition.** Each route has an explicit plan, a list of
steps returned in the response. The one open decision, which files to read when
the user didn't name any, goes to the model planner. It sees the search results
and the allowed paths, and must return JSON choosing at most two paths from that
list. Two safeguards apply:

- If the planner fails, times out, or returns an unknown path, a rule-based plan
  takes over.
- The model can't veto evidence. If it calls the results unrelated, but a
  *code* file in the search results contains a subject word from the task, the
  rule plan is used. A documentation-only mention ("this repo has no payment
  code") doesn't count. The agent abstains only when both checks agree that
  nothing is related. Matching is literal: a singular form must match a whole
  word, so "redis" doesn't match "redirect".

**Tool calling and controlled execution.** `AgentToolbox` exposes five read-only
tools: `list_files`, `search_code`, `read_file`, `outline_file` and
`static_checks`. The model never executes anything; it can only *propose* paths,
which are validated. Every call passes the same gate:

1. The tool must be on the allowlist.
2. The repository ID comes from the URL, never from model output.
3. A path must be an indexed file of that repository. Traversal text and other
   repositories' files are rejected and recorded as `rejected`.
4. A per-run budget of 10 tool calls applies; later calls are recorded as
   `skipped`.
5. An evidence budget of 6,000 characters and 150 lines per read applies; reads
   are cut at whole lines and the trace says so.
6. Expected failures (database, provider, timeout, model mismatch) become a
   trace entry instead of an exception.

**Agent state and communication.** Agents don't chat with each other. They share
one typed object, `AgentRunState`, which works as a blackboard:

- the Retrieval Agent writes `Evidence` items (`E1`, `E2`, … with path and line range)
- specialists read the evidence and write `Findings`
- the Supervisor records each hand-off in the trace

**Context management.** Small models have small context windows, so the agents
work within these limits:

- Evidence is bounded by characters and lines.
- The outline tool gives the model a whole-file map even when only part of the
  file fits.
- Evidence lines are numbered (`41| code`), so the model can cite exact lines.

**Grounding and validation.** The model returns JSON with an evidence ID and a
line range per finding. `AgentSynthesis.Parse` keeps a finding only if:

- the evidence ID exists,
- the range lies inside that evidence block,
- the title and detail are non-empty and within length limits,
- the severity (for reviews) is `low`, `medium` or `high`.

Invalid findings are dropped and counted in the trace. The displayed file path
always comes from the evidence record, never from model text.

**Error handling and recovery.** Each layer degrades to the next useful result:

| Failure | Result |
|---|---|
| Planner fails | Rule-based plan |
| Analysis model fails or returns no valid findings | `partial`: the deterministic outline, cited at declaration lines |
| Review model fails | `partial`: the deterministic rule findings |
| Search tool fails | `failed`, with the reason in the trace |
| Run deadline exceeded | `partial` if findings exist, otherwise `failed` |
| Trace storage fails | The result is still returned with `runId: null` and a `persist_run failed` trace step |

**Abstention.** Analysis and review abstain (`abstained`) when the planner and
rules agree that nothing related was found. After synthesis the rules differ by
agent:

- **Code Analysis Agent:** abstains when the model sets `sufficient=false`,
  even if it also described the unrelated code it was shown.
- **Review Agent:** abstains only when `sufficient=false` comes with no valid
  findings, because a validated defect is a fact about the code that was read.
  In evaluation, `qwen3:4b` once returned five valid auth findings together with
  `sufficient=false`.

A file the user named explicitly is always in scope, so the model can't dismiss
it.

**Observability.** Every step records agent, action, status
(`ok`, `fallback`, `timeout`, `failed`, `rejected`, `skipped`), duration and a
detail string. Details hold names, counts, line ranges and token counts, never
source text. Runs are stored in `agent_runs` and can be reopened from the UI's
"Recent runs" list or with `GET …/agents/runs/{runId}`.

## Evaluation

`dotnet run --project evaluation/RepoMind.Evaluation -- --mode agents --repository-id <id>`
runs the ten labeled tasks in `evaluation/agents-tasktrail.json` and reports:

- route accuracy
- grounded tasks that cite an expected file, split into completed and partial
- correct abstentions and refusals
- citations outside indexed line ranges
- whether each run can be reloaded from the trace store
- latency

See the latest report in `evaluation/final-agents-report.md`.

## Known limitations

- The router is keyword-based. A question that uses "issues" casually
  ("Where are GitHub issues fetched?") goes to the Review Agent.
- Review rules are text heuristics and can produce false positives. Rule findings
  are labeled `rule` so they can be told apart from model findings.
- A valid citation proves *where* a claim points, not that every sentence the
  model wrote is correct.
- With `qwen3:4b` on CPU, a model step takes roughly 15–60 s, and the
  planner/verifier can still make wrong relevance judgments.
