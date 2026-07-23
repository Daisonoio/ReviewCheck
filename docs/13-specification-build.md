# 13 — Build specification (for an LLM implementing the project)

This document turns the analysis into **implementable contracts**. It's the operational reference for
an agent/LLM writing the code: what to build, in what order, with which data, contracts, and acceptance
criteria.

> Golden rule for the implementer: **when a design choice touches the cognitive experience, the
> constraint of [§1](#1-non-negotiable-constraints) wins, not implementation convenience.**

## 1. Non-negotiable constraints

These are requirements, not preferences. A PR that violates them is rejected in review.

> **Honest enforcement (`.md` agent + MCP).** Distinguish *guaranteed* from *instructed* — see
> [`GUARDRAILS.md`](../GUARDRAILS.md) §3:
> - **Deterministically guaranteed** by our code/schema: block structure, mandatory citations, absence
>   of verdict tools, "outcome = human decisions + confirmation", no-phone-home.
> - **Strongly instructed** to the host LLM (when running as an `.md` agent): the *presentation*
>   (on-screen co-presence, tone, rhythm). Reliable, not ironclad. Full guarantees on presentation
>   require the "standalone program" variant (our own interface).

1. **Co-presence of code + explanation.** A block's code and its explanation are always presented
   **together, at the same time and in the same view/output**, on every surface (web and terminal).
   Never "code on request", never separate views. *(Split-attention effect.)*
2. **Human-in-the-loop.** The system **does not approve, does not emit verdicts** ("correct"/"there's a
   bug"); there is no `approve_pr()` on the AI side. The AI *explains and asks*.
2-bis. **Explanation grounding.** The correctness of the LLM explanation **cannot be guaranteed**; the
   product must not depend on it. Every explanation is **anchored to line citations** (≥1); structural
   facts come from the **deterministic layer**, not the LLM; uncertainty is **declared**; unanchorable
   claims **degrade to verifiable facts only**.
3. **One block at a time — a default, not a cage.** In focus mode only the current block is in the
   foreground, but its **edges are always visible** and a related block is peekable on demand; a **pass
   over the seams** covers cross-block bugs (anti-isolation, against C2).
4. **Guided reading order**, not alphabetical/by-file.
5. **State always saved and restorable** without loss (interruption tolerance).
6. **User controls the stimulus**: adjustable density/verbosity, no imposed time limit; on graphical
   surfaces `prefers-reduced-motion`/`prefers-color-scheme`.
7. **No dark patterns**: no punitive streaks, artificial urgency, or pushy notifications.
8. **Local / privacy by construction**: no backend, no DB; the code **does not leave the user's
   machine** and goes only to **their** LLM (host or BYO); only local temporary files; the tool **does
   not phone home**; local token at minimal scope, never logged.
9. **Accessibility**: plain language, keyboard navigation; on graphical surfaces WCAG 2.2 AA + ARIA.

## 2. Target architecture for v1

The product is a **local add-on (MCP server)**: **no backend, no database**. Implementation summary:

```
  Host agent (Claude Code/Cursor) ──▶ ReviewCheck (local MCP server)
                                        ├─ Source reader:  (A) local git  |  (B) platform PR
                                        ├─ Analysis Pipeline (Roslyn, graph, blocks, seams)
                                        ├─ LLM adapter (host  OR  BYO key)
                                        └─ Session store = LOCAL FILE (no DB)
```

- **Two modes (the source changes, the engine doesn't):**
  **A (primary)** = **local** pre-PR diff (git, no token/network), outcome = understanding + corrections
  to apply (nothing to post); **B (secondary)** = a **PR**, outcome postable to the platform.
- **MCP interface**: exposes the tools to the host agent → [`spec/mcp-tools.json`](../spec/mcp-tools.json).
- **Source reader**: (A) reads the local `git diff`; (B) reads a PR and **posts the outcome** with the
  user's **local token** (GitHub in v1, Azure DevOps later).
- **Analysis Pipeline**: §5 (runs locally).
- **LLM adapter**: host (host agent) or **BYO key** — always the user's LLM.
- **Session store**: **local file** → §3 and [`spec/session-state.schema.json`](../spec/session-state.schema.json).

**Default stack**: MCP server in **C# / .NET 8** (the official **`ModelContextProtocol`** SDK);
structural analysis with **Roslyn** (`Microsoft.CodeAnalysis.CSharp`); LLM via a **host-or-BYO**
abstraction; state in **local JSON files** (`System.Text.Json`); platform client with **Octokit.NET**
(GitHub) and an Azure DevOps client later. OSS distribution as a **.NET global tool**
(`dotnet tool install -g`) with SBOM + signing.

## 3. Data model — local session state (no DB)

**There is no database.** State lives in a **local file** per review; full schema in
[`spec/session-state.schema.json`](../spec/session-state.schema.json). Main structures (types in
`ReviewCheck.Core` as C# records, validated by a domain validator):

- **Session**: `session`, **`source`** (`{type:"local", ref}` for the local diff — Mode A — or
  `{type:"pull_request", platform, repo, pr}` — Mode B), `head_sha`, `title`.
- **Block**: `id`, `order_index`, `title`, `intent`, `files`, `code`, `explanation` (with
  `citations` ≥1 and `uncertainty`), `related_block_ids`, **`status`** (`pending`/`accepted`/
  `correction_requested`), `note` (if a correction).
- **BlockRelation**: `from`, `to`, `kind` (deterministic graph for order/map/seams).
- **interaction_points**: seams to verify (F13), derived from the graph.
- **progress**: `current_block_id`, `completed_block_ids`, `resumed_context` (resume, F7).
- **prefs**: local preferences (density/verbosity).

Domain invariant (`ReviewCheck.Core`): a `Block` is **invalid without both `code` and
`explanation.citations`** (co-presence + grounding, §1). The review outcome is **derived** from the
blocks' `status`, never decided by the AI.

## 4. MCP contracts (the "product")

This is **the** product contract: the tools the host agent calls. Full schema in
[`spec/mcp-tools.json`](../spec/mcp-tools.json). Rules: presentation + recording of **human
decisions**; the outcome is the **sum** of those decisions + **explicit confirmation** (constraint
§1.2). Whoever presents a block always returns **`code` + `explanation` together** (§1.1).

```jsonc
get_review_plan(source)   // source = {type:"local", ref?} (A, local git) | {type:"pull_request", platform, repo, pr} (B)
   → { session, title, blocks[], interaction_points[], first_block: Block }   // A: no token/network
next_block(session)          → { block: Block, position, progress_pct }
get_block(session, block_id) → { block: Block }        // also to "peek" at a related block (F4)
accept_block(session, block_id)               → { ok, block_status: "accepted" }
request_correction(session, block_id, note)   → { ok, block_status: "correction_requested", note_id }
review_status(session)   → { index, total, progress_pct, accepted, corrections }
submit_review(session, confirm)   // outcome = sum of human decisions; depends on the source
   → { outcome, posted, summary, notes[], undecided_blocks[] }   // fails if undecided_blocks ≠ []
   // MODE A (local): does NOT post. outcome "ready_to_proceed" | "corrections_to_apply"; notes = actionable to-do
   // MODE B (PR): with confirm=true it posts. outcome "approve" | "request_changes" | "comment_only" (author→D4)

Block = { id, title, intent, code /*ALWAYS*/, related_block_ids[],
          explanation: { what, why, link,
                         citations: [{file, lines}] /*≥1, grounding*/,
                         uncertainty: string|null } }
```

Rendering rule for MCP/CLI clients: print **`code` and, right next to/below it, `explanation`**, never
in separate requests. No tool judges or approves: `submit_review` is mechanical (sum of the `status`)
and requires human confirmation.

## 5. Analysis Pipeline (algorithm)

Implementation steps:

1. **Fetch & parse** — from the **source** (A: local `git diff` on working/staged/range; B: PR from the
   platform): unified diff + full involved files + metadata (commit message/description = intent signal).
2. **Structural analysis (deterministic)** — **Roslyn** for C#: maps hunks to semantic boundaries
   (method/class/property) and, via the semantic model, resolves symbols **across files**. v1: **C#**
   (further languages later).
3. **Dependency graph (deterministic)** — who defines/uses/calls what → `block_relations`, from
   Roslyn's semantic model.
4. **Block segmentation** — groups hunks into intent units by combining: same symbol/feature,
   co-change, + LLM refinement for the intent label. Constraint: small, coherent blocks (working-memory
   budget), **not** "one file = one block".
5. **Reading order** — topological sort over the graph (definitions→config→usage→effects→tests) with a
   deterministic fallback if the graph is incomplete.
6. **Explanations (LLM)** — per block: `what`/`why`/`link` over **targeted context** (block + graph
   neighbours + PR intent), in a *"describe and ask"* style, never verdicts. **Grounding (§1 2-bis):**
   the structural facts passed to the LLM come from steps 2–3 (it does not reinvent them); the output
   includes **`explanation.citations` (≥1 line)** and, if anchoring is weak,
   **`explanation.uncertainty`**; unanchorable claims degrade to verifiable facts only.
7. **Map/relations** — serializes the block graph → nodes/edges (textual edges; a rich visual map is an
   extension).
8. **Interaction points (seams, F13 / against C2)** — **the structural junction is deterministic**
   (from the graph: "this return value is used by N callers"), **the judgment is not**: whether each
   caller handles the case is an invitation to the human, not a defect found by the AI. Grounded
   attention-pointers, never verdicts; the LLM *phrases* them in natural language, it does not judge
   them. Computable from `block_relations`; **partial coverage** if the graph is incomplete → declare
   it (D5).

**Robustness**: steps 2/3/5/8 are deterministic; the LLM is used only in 4 (label) and 6 (explanation).
If a language is unsupported → **graceful degradation**: per-file/symbol segmentation without rich
explanations (focus + chunking still work).

**All local**: the pipeline runs on the user's machine; no shared server cache (an optional **local**
per-commit cache, under the user's control).

## 6. Sources and interfaces (no backend)

There is no REST API of ours. Two **sources** in; two **interfaces** out:

- **Source A — local git (primary):** reads `git diff` (working/staged/range). **No token, no
  network.** Outcome: *nothing is posted* — the user gets understanding + corrections to apply.
- **Source B — platform (secondary):** GitHub / Azure DevOps, consumed with the **user's local token** —
  **reads** the PR and, at the end of the review, **posts the outcome** (approve / request-changes /
  `comment_only`) with **explicit confirmation**. Minimal token scope.
- **Toward the host agent:** the **MCP tools** of §4 ([`spec/mcp-tools.json`](../spec/mcp-tools.json)),
  independent of the source.

## 7. v1 scope — build checklist

Recommended order (each item is verifiable).

- [ ] MCP server scaffold + `ReviewCheck.Core` (C# record types, co-presence + grounding invariant).
- [ ] **Source A (primary)**: reading the **local diff** via git (working/staged/range) — no token/network.
- [ ] **Source B (secondary)**: GitHub — **reads** the PR (local token) and **posts** approve/request-changes/comment_only.
- [ ] v1 pipeline on **C#** (Roslyn): parse → structural → graph → segmentation → order → explanations → seams.
- [ ] LLM adapter **host-or-BYO** (host agent; fallback to user key) with grounding.
- [ ] Session store on a **local file** (save-and-resume, F7); no DB.
- [ ] MCP tools: `get_review_plan`/`next_block`/`get_block`/`accept_block`/`request_correction`/`review_status`/`submit_review`.
- [ ] **Co-presence** code+explanation on every output (F1/F3/F4) + **visible edges** (F3) + **seams** (F13, against C2).
- [ ] Outcome by source: **A** = summary + corrections to apply (no posting); **B** = posting with **explicit confirmation** (F9).
- [ ] Local security: token in keychain/OS, never logged; session files with restricted permissions + cleanup; **the tool does not phone home**.
- [ ] OSS distribution: installable package (`dotnet tool`) + SBOM + signing.
- [ ] **Out of v1** (Phase 2): standalone CLI, IDE extension, Azure DevOps/GitLab, rich visual map, timeboxing (F11).

## 8. Definition of Done (v1)

- Inside a host agent (Claude Code/Cursor), a reviewer completes a guided review **end-to-end** and
  **interrupts and resumes without loss** (local file), in **both modes**: **A (primary)** on the
  **local pre-PR diff** → summary + corrections to apply (no posting); **B** on a **PR** → outcome
  posted to GitHub (approve / request-changes / comment_only).
- In **every** block output, code and explanation are **co-present**; the `explanation` has ≥1 **citation** (grounding).
- In **every** block output the **edges to related blocks are visible**, and there is a **pass over the seams** (F13).
- The outcome is the **sum of human decisions** + explicit confirmation; the system **does not** judge or approve on its own.
- **No data leaves the user's machine** toward a ReviewCheck server; the code goes only to their LLM (host/BYO).
- Works with a **host** LLM and with a **BYO key**; token handled securely.
- Stimulus preferences respected; full keyboard navigation; AA contrast.

## 9. "Where do I find what" map

| You need… | Go to |
|---|---|
| How it's used (walkthrough) | [12](12-flow-example.md) |
| **Implementable contracts** | **this document** |
| MVP roadmap + technical gates | [22](22-mvp-execution-roadmap.md) |
| Build plans (MVP-1/2/3) | [23](23-mcp-stub-first-plan.md) · [24](24-pipeline-plan.md) · [25](25-llm-plan.md) |
| Agent: WHAT/HOW + recovery | [21](21-development-plan.md) |
| Invariants and enforcement | [`GUARDRAILS.md`](../GUARDRAILS.md) |

> **Note (essential set):** the extended rationale — why the product exists, cognitive basis,
> market, UX, extended architecture, security — lives in the
> [`ReviewCheckOLD`](https://github.com/Daisonoio/ReviewCheckOLD) repo (analysis documents `00`–`11`, `14`–`18`).