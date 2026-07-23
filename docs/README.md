# ReviewCheck — essential documents

Product: **local add-on (MCP server)** for Claude Code/Cursor — no backend, no database.

## The documents (in order of use)

### Contracts and invariants — *source of truth (always open)*
| Doc | What you'll find |
|---|---|
| [`GUARDRAILS.md`](../GUARDRAILS.md) | the guardrails and **how each is enforced** (deterministic vs instructed) |
| [`13 — Build specification`](13-specification-build.md) | implementable contracts, §1 constraints, Definition of Done |
| [`spec/mcp-tools.json`](../spec/mcp-tools.json) | the 7 MCP tools + the `Block` schema |
| [`spec/session-state.schema.json`](../spec/session-state.schema.json) | the session state on a local file |

### Build plans — *the how, milestone by milestone*
| Doc | Milestone | What you build |
|---|---|---|
| [`22 — MVP execution roadmap`](22-mvp-execution-roadmap.md) | — | MVP scope + **technical gates** |
| [`23 — MCP stub-first`](23-mcp-stub-first-plan.md) | MVP-1 | MCP server + the `IReviewProvider` seam |
| [`24 — Pipeline`](24-pipeline-plan.md) | MVP-2 | diff → structural blocks (Roslyn, graph) |
| [`25 — LLM`](25-llm-plan.md) | MVP-3 | grounded explanations (completes the blocks) |

### Agent and behavior
| Doc | What you'll find |
|---|---|
| [`agent/reviewcheck.agent.md`](../agent/reviewcheck.agent.md) | the product agent (the artifact) |
| [`21 — Agent plan`](21-development-plan.md) | **WHAT/HOW** + the **R1–R10** recovery matrix |
| [`12 — Usage flow example`](12-flow-example.md) | end-to-end walkthrough (behavioral fidelity) |

## Minimal path to start writing code

```
GUARDRAILS.md + spec/ + docs/13   (contracts/guarantees)
        → docs/22                 (scope + gates: where you are)
        → docs/23 → 24 → 25       (the build plans, in dependency order)
        → agent.md + docs/21      (the agent and recovery)
        → docs/12                 (the expected behavior)
```