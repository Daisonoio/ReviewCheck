# 22 — MVP execution roadmap (technical gates only)

> **This document is the active execution track.** For MVP purposes it supersedes the gating sequence
> of the full-branch documents (`docs/14`, `docs/18`), which remain valid as the **full product vision**
> and as a reference for the contracts.

---

## 0. Gating principle

A gate blocks progress **only** if it is **technical**: i.e. verifiable by a machine (tests, schema,
CI, automated eval), with no people needed. Anything that requires **human judgment or a user study**
is not a gate: it is an **activity**, and it comes **after** the MVP — because the MVP is precisely the
instrument used to run it.

### REMOVED gates (non-technical → deferred to after the MVP)

| Former gate | Why it isn't a gate | Where it goes |
|---|---|---|
| **Phase 0 / M0 — cognitive validation with ND users** (NASA-TLX, comprehension, lived experience) | requires people + an artifact to show; **not passable before the MVP** | **post-MVP** activity (§5) |
| **Defect-detection study** (local vs systemic vs raw diff) | requires users and seeded bugs; it's research, not build | **post-MVP** activity (§5) |
| **Beta with real users** | requires the MVP to be ready | **post-MVP** activity (§5) |
| **go/no-go informed by a study** | depends on the two above | **post-MVP** activity (§5) |

> These don't disappear from the product mission: they **stop blocking** the build. They run once the
> MVP is in hand.

### KEPT gates (technical → they really block)

All automatable, no person needed:

| ID | Technical gate | How it's verified |
|---|---|---|
| **G-CI** | lint + build + tests green | CI |
| **G-SCHEMA** | a `Block` without `code` or without `citations` (≥1) is **not constructible/valid** | unit test on `ReviewCheck.Core` (record + validator) |
| **G-NOVERDICT** | no verdict/approval tool exists | absence of the tool in `ReviewCheck.Mcp` (structural check) |
| **G-ROUNDTRIP** | session state is reconstructed after an interruption | round-trip test on a local file |
| **G-NOPHONE** | no network beyond the configured LLM (+ platform in Mode B) | network test in CI |
| **G-E2E** | the MCP tools return **co-present** blocks end-to-end | integration test on a sample repo |
| **G-RECOVERY** | the R1–R10 commands restore the action by re-calling the tool | integration test (protocol [`docs/21`](21-piano-sviluppo-agente.md) §4.2) |
| **G-GROUNDING** | the LLM adapter **rejects** output without `citations` (≥1) | unit test on `ReviewCheck.Llm` |

> **`evals/` suite removed** (current choice): during the MVP, grounding and no-verdict are verified
> with **targeted unit tests** (`G-GROUNDING`, `G-NOVERDICT`), not with an eval suite. The `evals/`
> folder has been **deleted from the repo**; the suite will be **rebuilt and reintroduced as a gate once
> the MVP is complete** (§5).

---

## 1. What the MVP is (minimal showable scope)

**Target demo (one sentence):** *"I open Claude Code, point at a local diff, and get a guided,
block-by-block review — code + explanation together, with citations — I accept or request a correction,
and at the end I have the list of corrections to apply. All local."*

**Inside the MVP:**
- **Mode A** only (local diff via `git`, **no token, no network** toward platforms).
- **One language** analyzed: **C#** (via Roslyn; other languages later).
- The **7 MCP tools** working against a real (even if simple) pipeline.
- **`agent.md`** loaded in Claude Code, with co-presence, grounding, and **recovery R1–R10**.
- Mode A closure: **corrections list** / "ready to proceed". No posting.

**Out of the MVP (post-demo):**
- Mode B (GitHub PR + token + posting).
- Python and other languages.
- Full hardening (keychain, SBOM, signing), OSS packaging.
- Standalone CLI, IDE extension, other platforms.

---

## 2. Strategy: vertical slices, stub-first

Not layer-by-layer (first the whole agent, then the whole MCP), but **vertical slices**: first an
end-to-end **stubbed** skeleton that runs in Claude Code, then the stubs are replaced with the real
substance. This gives you an **integration loop from day one** and lets you test recovery **right away**.

---

## 3. MVP milestones (each with its technical gate)

### MVP-1 — Walking skeleton (stub-first) 🎯 *unblocks everything*
**Goal:** the agent runs in Claude Code against a **stubbed** MCP.
- `ReviewCheck.Core`: the `Block` type (C# record) with co-presence + grounding invariants.
- `agent/reviewcheck.agent.md`: skeleton (phases F0–F5 + recovery R1–R10).
- `ReviewCheck.Mcp`: the 7 tools returning **fixture blocks** — fake but **schema-conformant**
  (with `code`, `citations` ≥1, `uncertainty`).
- **Gate:** **G-CI**, **G-SCHEMA**, and a first **G-E2E/G-RECOVERY** against the stubs — in Claude Code
  you see code+explanation together; "show the code" / "with citations" / "uncertainty?" work.

> From here on the agent **doesn't change structure**: you only swap *what* is behind the tools.

### MVP-2 — Real pipeline: diff → blocks
**Goal:** the blocks become **real** (from the local diff, not fixtures).
- Source reader `git diff` (working/staged/range) — **G-NOPHONE**.
- **Roslyn** (C#): hunk → symbols, with cross-file resolution from the semantic model.
- Relation graph (defines/uses/calls) → `related_block_ids` + `explanation.link`.
- Block segmentation + reading order (with deterministic fallback).
- **Gate:** on a sample diff, `get_review_plan` returns real ordered blocks; **no block =
  whole file**; the `citations` point to real lines of the block.

### MVP-3 — Real grounded explanations
**Goal:** `explanation` produced by the LLM, anchored.
- `ReviewCheck.Llm`: **BYO key** adapter (host/sampling later, behind the same interface).
- A "describe and ask" prompt, `what/why/link` + `citations` (≥1) + `uncertainty`; the adapter
  **rejects** output without citations.
- **Gate:** **G-GROUNDING** (unit test: the adapter rejects output without `citations`); **G-NOVERDICT**
  (absence of the verdict tool). *No `evals/` suite in this phase.*

### MVP-4 — Full loop (Mode A) + resume
**Goal:** the end-to-end review closes with the corrections list.
- `ReviewCheck.Session`: store on a local file, save-and-resume.
- `accept_block` / `request_correction` / `review_status` / `submit_review` (Mode A).
- **Gate:** **G-ROUNDTRIP** (interrupt→resume); full **G-E2E** (real diff → review → corrections
  list); **G-RECOVERY** R1–R10 against the real pipeline.

**➡️ End of MVP-4 = showable MVP.** The demo of §1 runs on a real local diff, in Claude Code.

---

## 4. Gate summary by milestone

| Milestone | Technical gates to pass |
|---|---|
| MVP-1 | G-CI, G-SCHEMA, G-E2E(stub), G-RECOVERY(stub) |
| MVP-2 | G-NOPHONE, G-E2E(real diff, blocks) |
| MVP-3 | G-GROUNDING, G-NOVERDICT |
| MVP-4 | G-ROUNDTRIP, G-E2E(full), G-RECOVERY(real) |

No human/social gate on the path. The MVP is reached with automated checks only.

---

## 5. After the MVP (the former-gate activities, now runnable)

With the MVP in hand you can finally run — **as activities, not as blockers**:
- **Cognitive validation** with ND users (NASA-TLX, comprehension, lived experience).
- **Defect-detection** (local vs systemic vs raw diff).
- **Beta** and feedback collection → informed product decisions.

And the deferred technical extensions:
- **Rebuilding the `evals/` suite and reintroducing it as a gate** (`G-EVAL`: violations of
  `copresence`/`grounding`/`hitl`/`verdict` = 0 in CI) — the folder was removed, it needs to be rewritten.
- **Mode B** (GitHub), **Python**, **hardening + OSS packaging** (keychain, SBOM, signing), then
  Phase 2 (CLI, IDE, other platforms).

---

## References

- **Agent plan (WHAT/HOW/recovery):** [`docs/21`](21-piano-sviluppo-agente.md)
- **MCP contracts:** [`spec/mcp-tools.json`](../spec/mcp-tools.json)
- **Enforcement (deterministic/instructed layers):** [`GUARDRAILS.md`](../GUARDRAILS.md)
- **Full vision / rationale (with the original social gates):** `docs/14`, `docs/18` (full branch)

**Status:** MVP roadmap with technical gates only. Next step: **MVP-1 (walking skeleton, stub-first)**.