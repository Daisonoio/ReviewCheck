# ReviewCheck

**Guided, step-by-step code review that helps you actually understand the code an AI wrote for you — so you can own it, not just approve it.**

![status](https://img.shields.io/badge/status-MVP%20built%20·%20local-brightgreen)
![tests](https://img.shields.io/badge/tests-124%20passing-brightgreen)
![type](https://img.shields.io/badge/form-local%20MCP%20add--on-blueviolet)
![privacy](https://img.shields.io/badge/privacy-local%20only%20·%20no%20backend-brightgreen)
![license](https://img.shields.io/badge/license-MIT-blue)
![PRs](https://img.shields.io/badge/PRs-welcome-brightgreen)

> As AI agents write a growing share of our code, the scarce resource is no longer *writing* — it's
> **human understanding that scales**. ReviewCheck breaks a change into **small, ordered blocks**,
> explains each one *next to the code it describes*, and walks you through them **one at a time** —
> so you finish a review having genuinely understood what you're about to ship, and **you** keep the
> decision. Designed first for developers with ADHD / attention differences; useful for everyone.

> [!IMPORTANT]
> **Project status: MVP built and working locally.** The full deterministic core (MVP-1 + MVP-2) and
> the grounded LLM narration layer (MVP-3) are implemented, covered by **124 passing tests**, and
> verified end-to-end inside Claude Code: a local `git diff` → a guided, block-by-block review →
> accept / request-correction → outcome. C# (Roslyn) is the supported language; **Mode B** (posting a
> review to a PR) is the next milestone, not yet built. See [Getting started](#getting-started) to run it.
>
> **New here? Start with [`docs/README.md`](docs/README.md)** — the essential-docs index.

---

## Table of contents

- [The problem](#the-problem)
- [What ReviewCheck is](#what-reviewcheck-is)
- [How it works](#how-it-works)
- [Design principles (non-negotiable)](#design-principles-non-negotiable)
- [Architecture](#architecture)
- [Roadmap](#roadmap)
- [Repository structure](#repository-structure)
- [Getting started](#getting-started)
- [Contributing](#contributing)
- [Security & privacy](#security--privacy)
- [License](#license)

---

## The problem

Code review is where understanding should transfer and defects should be caught. In practice it's the
most-skipped step of the cycle — and AI-generated code makes it worse:

- Changes arrive **large and undifferentiated**: many files, no reading order, intent left implicit.
- The human brain can't hold that much new, interrelated information at once (working memory ≈ 4 chunks).
- So people **disengage** and approve with a defensive *"LGTM"* — precisely when the volume of code to
  review is exploding and the author often *asked for* the code rather than writing it line by line.

The result is a quiet erosion of **ownership**: nobody really understands, or feels responsible for,
the code that ships. For developers with **ADHD or attention differences**, this isn't friction — it's
a wall.

## What ReviewCheck is

A **local, open-source add-on** for AI coding environments (Claude Code, Cursor, Copilot, …), shipped
as an **agent definition (`.md`) + a local MCP server**. It takes a set of changes, splits it into
**coherent blocks**, and guides you through them:

- shows **code and explanation together**, with **line-level citations**;
- presents **one block at a time**, in a sensible **reading order**, with the **links** between blocks
  visible and the **seams** (where cross-block bugs hide) flagged;
- lets you **accept** or **request a correction** per block;
- produces the outcome as the **sum of your decisions** — the AI never gives a verdict.

**No backend. No database. No telemetry.** It runs on your machine, uses **your** LLM (your key or a
local model) and **your** repository access. Your code never leaves your infrastructure.

## How it works

Two sources, one engine:

- **Mode A — local diff, pre-PR (primary).** Review what an agent just wrote — the uncommitted /
  staged / local changes — **before** you open a pull request. Reads via `git`; **no token, no
  network**. The outcome is your understanding plus a **list of corrections to apply**.
- **Mode B — pull request (secondary).** Review a PR (yours or a teammate's); the outcome can be
  **posted** to GitHub / Azure DevOps (approve / request-changes, with a `comment_only` fallback when
  the platform forbids self-approval).

```mermaid
flowchart LR
    A["Changes<br/>(local diff · or a PR)"] --> P["ReviewCheck<br/>(local MCP server)"]
    P --> B["Blocks + reading order + seams<br/>code &amp; explanation, grounded"]
    B --> H{"You, block by block"}
    H -->|accept| H
    H -->|request correction| H
    H --> O["Outcome = sum of your decisions<br/>(corrections list · or posted review)"]
    P -. "your LLM (BYO key / local)" .-> L["LLM"]
```

<details>
<summary>Example (inside Claude Code, Mode A)</summary>

```
You:  review the changes I just wrote, before I open the PR

Agent: [get_review_plan({type:"local"})]   ← reads git diff locally, no token/network
  Rate limiting for the public API. 6 blocks.
  Seam to check (from the graph): "allow() can return False → check every caller handles it".

  ── Block 1/6 ──  rate_limiter/limiter.py (new)
    class TokenBucket:
        def allow(self, key): ...
  WHAT: token-bucket limiter, the core of the change.        (cites limiter.py:1-11)
  WHY:  decides whether a request passes or is rejected.
  LINKS: used by the middleware (block 3), params from block 2.
  ⚠ Uncertainty: could not resolve `_refill` (defined elsewhere).
  Accept this block, or request a correction?

You:  what if key is null? request a correction
Agent: recorded. Next block?
  ...
  → Outcome: CORRECTIONS TO APPLY (nothing posted).
    • block 1: "handle key=null in allow()"
    Fix these, then open the PR.
```
</details>

## Design principles (non-negotiable)

These are enforced constraints, not preferences (see [`GUARDRAILS.md`](GUARDRAILS.md) for the
guaranteed-vs-instructed model):

| Principle | Meaning |
|---|---|
| **Co-presence** | Code and its explanation are always shown together. Never "explanation only". |
| **Grounding** | Every explanation cites specific lines; structural facts come from a deterministic graph, not the LLM; uncertainty is declared. **The code is the source of truth — the explanation is a guide.** |
| **Human-in-the-loop** | The AI explains and assists; it **never** judges or approves. The outcome is the sum of your per-block decisions + an explicit confirmation. |
| **Local by construction** | No backend, no database, no phone-home. Code goes only to *your* LLM. |
| **No dark patterns** | No streaks, no artificial urgency, no surveillance metrics. You control verbosity and pace. |

## Architecture

A local MCP server with a deterministic core and the LLM as a thin, grounded layer on top:

```mermaid
flowchart TB
    Host["Host agent (Claude Code / Cursor / Copilot)"] -->|MCP tools| RC
    subgraph RC["ReviewCheck — local MCP server"]
        SR["Source reader<br/>(git diff · or platform PR)"]
        PL["Analysis pipeline<br/>Roslyn · graph · blocks · order · seams"]
        LA["LLM adapter<br/>(your key / local model)"]
        SS["Session store<br/>local JSON file"]
    end
    RC -->|"read / (Mode B) post"| GH["GitHub / Azure DevOps"]
    RC -->|"targeted context"| LLM["Your LLM"]
```

- **Deterministic backbone** (Roslyn parsing + semantic model, dependency graph, reading order, seams) does the
  reliable work; the **LLM only interprets** (intent labels + explanations) and its output is bound to
  citations. This keeps the tool robust and testable, and means the product never *depends* on the LLM
  being correct.
- Design contracts in [`docs/13`](docs/13-specification-build.md) and [`spec/`](spec/); the build plans run
  [`docs/22`](docs/22-mvp-execution-roadmap.md) → [`23`](docs/23-mcp-stub-first-plan.md) →
  [`24`](docs/24-pipeline-plan.md) → [`25`](docs/25-llm-plan.md).

## Roadmap

| Phase | Focus | Status |
|---|---|---|
| **1 — v1 (MVP)** | Local MCP add-on: Mode A (local diff), C# (Roslyn), BYO-key LLM, the full block-by-block flow. | ✅ **Built** — Mode A, Roslyn pipeline, grounded LLM, 7 MCP tools, session persistence, recovery commands. Mode B (post to GitHub) is next. |
| **2** | Standalone CLI, dedicated IDE extension, Azure DevOps / GitLab, rich visual concept map. | ⬜ Planned |
| **3** | Recommended local models, per-repo codebase memory, personalization — all local. | ⬜ Planned |
| **Ongoing** | Validation study with ADHD/ND users: does guided review improve comprehension *and* defect detection vs a raw diff? | ⬜ Planned |

## Repository structure

```
src/                  The .NET solution (net8.0):
  ReviewCheck.Core        Immutable domain types + BlockGuard (co-presence + grounding)
  ReviewCheck.Platform    Unified-diff parser + LocalDiffReader (git, local process)
  ReviewCheck.Pipeline    Roslyn analysis P1–P8: graph, blocks, order, citations, seams
  ReviewCheck.Llm         ILlmProvider (BYO key) + LlmAdapter + rubric + FactsNarrator floor
  ReviewCheck.Session     Session persistence (local JSON under .reviewcheck/)
  ReviewCheck.Mcp         The MCP server: the 7 tools + narrator wiring
tests/                One xUnit project per src project (124 tests)
docs/                 Contracts (13), MVP plans (22–25), agent plan (21), flow example (12), index (README).
spec/                 Machine-readable contracts: mcp-tools.json, session-state.schema.json
agent/                The product agent definition (reviewcheck.agent.md)
GUARDRAILS.md         Guardrails and how each is enforced
```

> The build plans that produced `src/` are in
> [`docs/22`](docs/22-mvp-execution-roadmap.md)–[`25`](docs/25-llm-plan.md).

## Getting started

**Prerequisites:** the [.NET 8 SDK](https://dotnet.microsoft.com/download) and `git` on your `PATH`.

### 1. Build and test

```bash
git clone https://github.com/Daisonoio/ReviewCheck.git
cd ReviewCheck
dotnet test        # 124 tests should pass
```

### 2. Publish the MCP server

```bash
dotnet publish src/ReviewCheck.Mcp/ReviewCheck.Mcp.csproj -c Release -o ./bin/mcp
```

This produces `bin/mcp/ReviewCheck.Mcp.exe` (Windows) / `ReviewCheck.Mcp` (Linux/macOS).

### 3. Register it in your host agent

For **Claude Code**, add the server to your `.mcp.json` (the key **must** be `mcpServers`):

```json
{
  "mcpServers": {
    "reviewcheck": {
      "type": "stdio",
      "command": "/absolute/path/to/ReviewCheck/bin/mcp/ReviewCheck.Mcp.exe",
      "env": {
        "REVIEWCHECK_REPO": "/absolute/path/to/the/repo/you/want/to/review",
        "REVIEWCHECK_ANTHROPIC_KEY": "sk-ant-..."
      }
    }
  }
}
```

Restart the host so it launches the server. On startup the server logs one line to stderr naming the
active narrator — `narrator: LLM (...)` or `narrator: facts-only (...)` — so you always know which path
is live.

### 4. Use it

Open your host agent in the target repository, make (or let the agent make) some changes, and ask:

> *review my changes with ReviewCheck*

It reads the local `git diff` (uncommitted changes, **including new untracked files**), splits it into
ordered blocks, and walks you through them — accept or request a correction per block, then a final
outcome that is the sum of your decisions.

### Configuration (environment variables)

| Variable | Effect |
|---|---|
| `REVIEWCHECK_ANTHROPIC_KEY` | Your Anthropic key (or `ANTHROPIC_API_KEY`). **Present → LLM explanations; absent → deterministic facts-only.** Never logged; code goes only to your account. |
| `REVIEWCHECK_LLM_MODEL` | Model for narration (default `claude-sonnet-5`). |
| `REVIEWCHECK_REPO` | Absolute path of the repository to review (defaults to the server's working directory). |
| `REVIEWCHECK_NARRATOR` | Set to `facts` to force the deterministic floor even with a key (demos, offline, cost control). |
| `REVIEWCHECK_PROVIDER` | Set to `stub` to use the fixture provider instead of the real pipeline (demos/tests without a repo). |

> **Design docs:** the contracts are the source of truth —
> [`docs/13-specification-build.md`](docs/13-specification-build.md) and [`spec/`](spec/); the build
> plans are [`docs/22`](docs/22-mvp-execution-roadmap.md)–[`25`](docs/25-llm-plan.md); the agent is
> specced in [`docs/21`](docs/21-development-plan.md).

## Contributing

The MVP is built and runnable — a good moment to extend it. Ways to help:

- **Mode B (post to a PR)** — the next milestone: read/post a GitHub review from the same block flow.
- **Language support** — additional language analyzers beyond C# (Roslyn).
- **Evals** — rebuild the capability suite (grounding, no-verdict, co-presence, human-in-the-loop);
  deferred until after the MVP (see [`docs/22`](docs/22-mvp-execution-roadmap.md) §5).
- **Cognitive-accessibility research** — help design/run the Phase-0 study with neurodivergent
  developers (*"nothing about us without us"*).
- **Docs** — English translation of the design docs.

Please read [`GUARDRAILS.md`](GUARDRAILS.md) first: contributions that violate the non-negotiable
constraints (e.g. adding a backend, or an "auto-approve" capability) can't be accepted, by design.

## Security & privacy

ReviewCheck handles source code — the most sensitive asset a software team has — so security is a
first-class concern, not an afterthought. The local, no-backend model dissolves whole classes of SaaS
risk; the residual focus is **local token handling**, **supply-chain integrity** of the OSS package,
**no phone-home**, and **indirect prompt injection** via untrusted repo content.

## License

[MIT](LICENSE).

---

<sub>ReviewCheck is built on a simple conviction: when an agent writes the code, the AI should help you
**understand** it — not understand it **for you**. Understanding is how you keep ownership; the decision
— and the responsibility — stay yours.</sub>