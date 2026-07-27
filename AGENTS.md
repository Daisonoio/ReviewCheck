# AGENTS.md — working in this repository

> Context for an **AI agent (or human) developing ReviewCheck**. This is the contributor-facing
> counterpart to [`agent/reviewcheck.agent.md`](agent/reviewcheck.agent.md), which is the *product*
> agent that end users run. Two different audiences:
>
> | File | Audience | Answers |
> |---|---|---|
> | **AGENTS.md** (this file) | an agent **building** ReviewCheck | how do I build, test, and extend this repo without breaking its guarantees? |
> | `agent/reviewcheck.agent.md` | an agent **running** ReviewCheck for a user | how do I guide a human through a review? |
>
> Treat this file as **infrastructure**: keep it in sync with the code, in the same PR.

## What this project is

ReviewCheck is a local **MCP server** (C# / .NET 8) that turns a code diff into a guided, block-by-block
review. A **deterministic core** (Roslyn → dependency graph → ordered blocks → line citations → seams)
does the reliable work; an **LLM is a thin, reined-in layer** that only narrates and is bound to the
citations. The product never depends on the model being correct.

## Golden-path commands

```bash
# Build everything (warnings are errors — see conventions)
dotnet build ReviewCheck.sln -c Release

# Unit tests (134) — one xUnit project per src project
dotnet test ReviewCheck.sln

# Capability eval suite — scores the guardrails over a corpus, exits non-zero on regression
dotnet run --project eval/ReviewCheck.Evals/ReviewCheck.Evals.csproj -c Release

# Publish the MCP server the host launches
dotnet publish src/ReviewCheck.Mcp/ReviewCheck.Mcp.csproj -c Release -o ./bin/mcp
```

CI (`.github/workflows/ci.yml`) runs build + tests **and** the eval suite as a gate. A green PR means
all three pass.

## Map of the repo

```
src/
  ReviewCheck.Core        Immutable domain types + BlockGuard (co-presence + grounding invariant)
  ReviewCheck.Platform    Unified-diff parser + LocalDiffReader (git, local process)
  ReviewCheck.Pipeline    Roslyn analysis: graph, segmentation, reading order, citations, seams
  ReviewCheck.Llm         Narrator seam: LlmAdapter (reined-in LLM) | FactsNarrator (deterministic floor)
  ReviewCheck.Session     Session persistence (local JSON under .reviewcheck/)
  ReviewCheck.Mcp         The MCP server: the 8 tools + per-review narrator selection
tests/                One xUnit project per src project
eval/                 Capability eval suite (see eval/README.md)
agent/ , .claude/     The PRODUCT agent definition + the Claude Code skill packaging
spec/                 Machine-readable contracts: mcp-tools.json, session-state.schema.json
docs/                 Design contracts (13), MVP plans (22–25), agent plan (21)
GUARDRAILS.md         The guarantees and how each is enforced
```

## The guarantees you must not break

These are the point of the product. If a change would weaken one, it is wrong by design — see
[`GUARDRAILS.md`](GUARDRAILS.md).

- **Co-presence + grounding** — every `Block` carries `Code` **and** ≥1 real line citation. Enforced by
  `BlockGuard.Ensure`, the single choke point every tool passes through. Don't route around it.
- **No verdict** — the tool describes and asks; it never says "correct / safe / approved". The outcome is
  the sum of the human's per-block decisions.
- **Grounding is computed, not invented** — citations come from the pipeline; the LLM narrates but never
  anchors. In `LlmAdapter`, `Code` and `Citations` are stapled verbatim.
- **Degrade, never fail** — no key / bad key / model down / rejected output → fall back to the
  deterministic facts narrative. A valid block always comes out.
- **Local by construction** — no backend, no telemetry, no phone-home. Code goes only to the user's own LLM.

Every one of these has a check in the eval suite. If you add a guarantee, add its eval.

## Conventions

- **Warnings are errors** (`Directory.Build.props` → `TreatWarningsAsErrors`). Keep the build clean.
- **Determinism in the pipeline** — same diff in, same blocks out. No randomness, no network, no clock in
  `ReviewCheck.Pipeline`. This is what makes it golden-testable.
- **Immutable domain types** — `record` types in `ReviewCheck.Core`; mutate working copies, return
  immutables.
- **The LLM speaks only through one seam** — `ILlmProvider`. Anything that stops a completion surfaces as
  `LlmUnavailableException` and means "degrade to facts", never a crash. Keys never appear in messages.
- **Tests live beside code** — one `*.Tests` project per `src` project; add tests in the matching one.
- **Contracts are source of truth** — if you change a tool's shape, update `spec/mcp-tools.json` in the
  same change.

## How to extend it (common tasks)

- **Add an eval case** — drop a `.cs` file into `eval/ReviewCheck.Evals/corpus/<case>/`. It is treated as
  an added file and flows through the real pipeline; no code change needed.
- **Add a capability check** — add a method in `eval/ReviewCheck.Evals/Capabilities.cs` and include it in
  the scorecard. Judge the property **independently** of the implementation.
- **Support another language** — the narrator/analysis is behind a seam; a new language is a new analyzer
  feeding `StructuralBlock`s, not a redesign. Keep the deterministic contract.
- **Tune the LLM narrative** — `ReviewCheck.Llm/PromptBuilder.cs` (the system-prompt library) and
  `ExplanationRubric.cs` (the output bouncer). Every model output passes the rubric before becoming a block.

## Where the deeper docs are

- Guarantees and enforcement: [`GUARDRAILS.md`](GUARDRAILS.md)
- Eval suite: [`eval/README.md`](eval/README.md)
- Design contracts: [`docs/13`](docs/13-specification-build.md), [`spec/`](spec/)
- Build plans: [`docs/22`](docs/22-mvp-execution-roadmap.md)–[`25`](docs/25-llm-plan.md)
