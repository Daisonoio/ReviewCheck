# 21 — Development plan for the ReviewCheck **agent**

> **Scope:** this plan concerns **only the agent** — i.e. the file [`agent/reviewcheck.agent.md`](../agent/reviewcheck.agent.md)
> and its **behavior** when running inside a host LLM (Claude Code / Cursor / Copilot).
> It does **not** concern the pipeline, the MCP server, or the projects: those are the *deterministic
> substance* and are covered elsewhere.
>
> **Sources of truth:** [`spec/mcp-tools.json`](../spec/mcp-tools.json) (contracts), [`GUARDRAILS.md`](../GUARDRAILS.md)
> (enforcement), [`docs/13`](13-specifica-build.md) §1 (constraints). In case of conflict, the spec wins.

This document is split into three parts answering three questions:
1. **WHAT** the agent does (observable behavior, per phase).
2. **HOW** it does it (mechanism: which tool, which fields, which on-screen rendering).
3. **Soft-guardrail recovery**: if one of the **non-deterministic** (instructed) actions doesn't happen,
   how the user can **request it specifically** from the LLM, anchored to the deterministic tool.

---

## 0. Mental model: two layers

The agent is the **rendering** (presentation); the MCP tools are the **substance** (guaranteed data).
Every agent action belongs to one of two layers (from [`GUARDRAILS.md`](../GUARDRAILS.md) §3):

| Layer | Who guarantees it | Examples | If it fails… |
|---|---|---|---|
| **Deterministic** | **our code** (MCP schema, absence of verdict tools) | `code` and `citations` always present in the `Block`; no `auto_approve`; outcome = sum of decisions | it can't fail silently: it's in the schema / covered by the technical gates in CI |
| **Instructed** (soft) | the **host LLM** following these instructions | *printing* code+explanation together, *showing* the citations, *declaring* uncertainty, the "describe and ask" tone, the one-block-at-a-time rhythm | it can fail → a **recovery procedure** is needed (Part 3) |

> **Recovery principle (the key to the whole plan):** every **instructed** action that can fail has a
> **user command** that forces it, and that command **re-calls the deterministic tool** that already
> holds the guaranteed data. Recovery doesn't "convince" the LLM to do better: it **re-serves the data**
> from the contract and asks it to render it. The substance is always there; recovery restores the *rendering*.

---

# PART 1 — WHAT the agent does

The agent walks the user through a review **one block at a time**, in six phases. For each: what must
happen on screen, and what the agent must **not** do.

### F0 — Start and onboarding
- **Does:** opens the session on the right source (default: local pre-PR diff), announces the **title**,
  the **number of blocks**, the **seams** to verify, and proposes the **first tiny step**
  ("shall we start with the first block?").
- **Doesn't:** dump all the blocks at once; ask the user to configure anything superfluous; promise
  "I'll find the bugs".

### F1 — Block presentation (the core)
- **Does:** prints **code + explanation together**, with the **line citations** and any **uncertainty**;
  shows the **edges** ("uses X, is used by Y").
- **Doesn't:** show the explanation alone; say "want to see the code?"; emit verdicts
  ("it's correct/safe"); invent relationships not present in the edges.

### F2 — Human decision
- **Does:** after each block explicitly asks **accept** or **request correction** (with a note);
  records the decision.
- **Doesn't:** advance on its own; decide for the user; interpret silence as consent.

### F3 — Navigating the edges (peeking)
- **Does:** on request, shows a **related block** without advancing the position ("peeking"), then
  brings the user back to the current block.
- **Doesn't:** lose the place; turn a peek into an advance.

### F4 — Interruption and resume
- **Does:** on resume it reads the **saved state** and picks up from the right point ("you were at block
  N, you had accepted 1–3").
- **Doesn't:** start over; re-propose already-decided blocks.

### F5 — Closure
- **Does:** with all blocks decided, produces the outcome as the **sum of the decisions**:
  - **Mode A (local):** summary + **list of corrections to apply** (or "ready to proceed");
    **posts nothing**.
  - **Mode B (PR):** **preview** of the outcome, then **explicit confirmation**, then posts.
- **Doesn't:** post without confirmation; approve/reject on its own; close with undecided blocks.

---

# PART 2 — HOW it does it (mechanism per action)

Here each behavior from Part 1 is reduced to: **tool called → fields used → on-screen rendering**.
This is the "how" the agent implementer must render in the `.md` file.

## 2.1 Action → tool → fields → rendering map

| Phase | Action | MCP tool | Fields used (from the schema) | Expected on-screen rendering |
|---|---|---|---|---|
| F0 | Open session | `get_review_plan` | `session`, `title`, `blocks[]`, `interaction_points[]`, `first_block` | "«title» — N blocks. Seams: …. Shall we start with the first?" |
| F1 | Show block | `next_block` (or `first_block` from `get_review_plan`) | `block.code`, `block.explanation.{what,why,link,citations,uncertainty}`, `block.related_block_ids`, `position`, `progress_pct` | code → explanation → citations → uncertainty → edges → question |
| F2 | Accept | `accept_block` | `ok`, `block_status:"accepted"` | "Marked as accepted. On to the next?" |
| F2 | Request correction | `request_correction` | input `note`; output `block_status:"correction_requested"`, `note_id` | "Correction recorded: «note». On to the next?" |
| F3 | Peek at a related block | `get_block` | `block` (complete) | shows the related block **without** advancing; then "back to the current block?" |
| F4 | Resume | `review_status` | `index`, `total`, `progress_pct`, `current_block_id`, `accepted`, `corrections` | "You were at N/T (X%), accepted A, corrections C. Resuming from here." |
| F5 | Close | `submit_review` | `outcome`, `posted`, `summary`, `notes[]`, `undecided_blocks[]` | A: corrections to-do; B: preview → confirm → post |

## 2.2 The "how" of presenting a block (F1, detail)

This is where the soft guardrails live or die. The **mandatory** rendering of a `Block`:

```
┌─ CODE              ← block.code (source of truth, with line numbers)
│  12:  private readonly Dictionary<string,int> _tokens = new();
│  13:  public bool Allow(string key) { ... }
│
│ EXPLANATION
│  What:  <block.explanation.what>
│  Why:   <block.explanation.why>
│  Citations: <each citations[].file:citations[].lines>   ← e.g. "cache.cs:12-13"
│  Uncertainty: <block.explanation.uncertainty | "none">
│
│ EDGES               ← block.explanation.link + block.related_block_ids
│  <link>   (peekable related: <related_block_ids>)
│
│ Decision: accept, or request a correction?
└─
```

**Rendering rules (the precise "how"):**
- **Fixed order:** code **first**, explanation **after**. Never invert, never omit the code.
- **Citations always visible:** each entry of `citations[]` is rendered as a traceable `file:lines`. The
  field is guaranteed ≥1 by the schema → there's **no excuse** for an explanation without a citation.
- **Explicit uncertainty:** if `explanation.uncertainty` is not `null`, it must be **shown**; if `null`,
  write "none" (don't invent confidence).
- **"Describe and ask" tone:** declarative sentences + a question; **never** verdict adjectives
  ("correct", "safe", "good").
- **A single question at the end:** accept / request correction. Don't advance without an answer.

## 2.3 The "how" of closure (F5, detail)

`submit_review` **fails** if `undecided_blocks[]` is non-empty → the agent **cannot** close with
undecided blocks (deterministic guarantee G6). Behavior by mode:

- **Mode A (`source.type:"local"`):** `posted` is **always** `false`. The agent presents `summary` +
  `notes[]` as an **actionable to-do**. Expected outcome: `ready_to_proceed` | `corrections_to_apply`.
- **Mode B (`source.type:"pull_request"`):** first `submit_review` **without** `confirm` → preview; the
  agent shows `outcome` + `notes[]` and **asks for confirmation**; only after an explicit yes does it call
  `submit_review({confirm:true})`. Outcome: `approve` | `request_changes` | `comment_only` (fallback if
  the platform forbids self-review).

---

# PART 3 — Soft-guardrail recovery procedure

> **What it solves:** the Part 2 actions marked as **instructed** (rendering of co-presence, citations,
> uncertainty, edges, rhythm, tone) depend on the host LLM and **may not happen**. This procedure gives
> the user, for **each** instructed action, a **command that requests it specifically**, and that restores
> it by **re-calling the deterministic tool** that already holds the data.

## 3.1 Operating principle

```
Instructed action didn't happen
        │
        ▼
The user utters the recovery command specific to that action
        │
        ▼
The agent RE-CALLS the deterministic tool (get_block / next_block / review_status)
        │
        ▼
The tool returns the GUARANTEED DATA (schema) for that action
        │
        ▼
The agent RENDERS exactly that action (co-presence / citations / uncertainty / edges / …)
        │
        ▼
Natural tone ("Here's the block's code.") — no apology, no loop
```

## 3.2 Recovery matrix (one row per instructed action)

Each instructed action → a **command** → the **tool** that guarantees its data → the guaranteed **field**
→ the **behavior** expected after recovery.

| # | Instructed (soft) action | Failure symptom | User command (synonyms) | Recovery tool | Guaranteed field backing it | What the agent does afterward |
|---|---|---|---|---|---|---|
| **R1** | **Co-presence** (code+explanation together) | shows only the explanation, or says "want to see the code?" | "show the code" · "co-presence" · "show me the code" | `get_block(session, block_id)` | `block.code` (**required**) | re-prints the block with **code first**, explanation after |
| **R2** | **Visible grounding** (line citations) | explains without pointing to the lines | "with citations" · "where in the code?" · "which lines?" | `get_block(session, block_id)` | `block.explanation.citations` (**minItems: 1**) | renders each `citations[]` as `file:lines` next to the sentence |
| **R3** | **Declared uncertainty** | presents everything as certain | "how sure are you?" · "uncertainty?" · "where are you uncertain?" | `get_block(session, block_id)` | `block.explanation.uncertainty` | shows the field; if `null` says "no uncertainty declared by the tool" |
| **R4** | **Visible edges** (relations) | doesn't mention related blocks/effects | "what does it touch?" · "relations" · "who uses it?" | `get_block(session, block_id)` | `block.explanation.link` + `block.related_block_ids` | lists the edges and the **peekable** related blocks |
| **R5** | **Peek at a related block** without advancing | loses the place / advances by mistake | "peek \<id\>" · "show me \<id\> without advancing" | `get_block(session, related_id)` then `review_status` | related `block` + `current_block_id` unchanged | shows the related block, then returns to `current_block_id` |
| **R6** | **One block at a time** (rhythm) | summarizes multiple blocks together | "one at a time" · "stop at this block" | `review_status` → then `get_block(current_block_id)` | `current_block_id`, `index`, `total` | realigns to the current block and shows **only** that one |
| **R7** | **"Describe and ask" tone** (no verdict) | says "it's correct/safe/approved" | "don't judge" · "describe and ask" · "no verdicts" | `get_block(session, block_id)` | absence of verdict fields in the schema (`forbidden_tools`) | re-presents descriptively + the accept/correct question |
| **R8** | **Per-block decision** (asks before advancing) | advances without asking | "wait, I haven't decided" · "go back" | `review_status` | `current_block_id`, `accepted`, `corrections` | returns to the undecided block and re-poses the question |
| **R9** | **First tiny step** (onboarding) | dumps the whole plan at once | "one step at a time" · "just the first block" | `get_review_plan` → uses `first_block` | `first_block` (only one) | shows **only** the first block and the question |
| **R10** | **Confirm before posting** (Mode B only) | about to post without asking | "don't post yet" · "let me see first" | `submit_review` **without** `confirm` | `outcome`, `notes[]`, `posted:false` | shows the preview and **waits** for explicit confirmation |

> **Note on R7/R8/R10:** these touch guardrails that are **also** deterministic (G4/G5/G6). Recovery here
> is a **presentation override**: the substance data/behavior is already guaranteed (no verdict tool
> exists; the outcome comes only from the decisions; `submit_review` doesn't post without `confirm`), but
> the command restores the correct **rendering** if the LLM gets it wrong.

## 3.3 Recovery behavior contract (rules for the host LLM)

When a recovery command is invoked, the agent **must**:

1. **Anchor, don't improvise.** Re-call the tool indicated in the table and use **its** fields. Don't
   reconstruct the block from memory.
2. **Render exactly the requested action.** R2 → show the citations; R3 → show the uncertainty; etc.
   Don't "compensate" with something else.
3. **Natural tone, no apology.** "Here's the block's code." / "The anchored lines are …". **Never**
   "sorry, I forgot" and **never** enter a loop of justification.
4. **Idempotence.** If the user repeats the same command, repeat the same anchored action, without
   frustration and without changing the outcome.
5. **Don't alter state.** Recovery **doesn't** decide, **doesn't** advance, **doesn't** post. R5/R6/R8
   realign but don't modify `accepted`/`corrections`.
6. **Tool failure = clear message.** If `get_block`/`review_status` doesn't respond, say so ("I can't
   recover the block; shall I reopen the session?") — **no silent crash, no invention**.

## 3.4 End-to-end example (R1 + R2 chained)

```
Agent: "This block introduces a cache to avoid recomputation."   ← code missing (R1 failed)

User: "show the code"
Agent: → get_block(session, "b3")
        ┌─ CODE
        │  12: private readonly Dictionary<string,int> _tokens = new();
        │  13: public bool Allow(string key) { Refill(key); ... }
        │
        │ EXPLANATION
        │  What: memoizes the counts per key
        │  Why: to avoid costly recomputation
        │  Uncertainty: Refill is defined in another partial file
        │  Decision: accept or request a correction?
        └─
        "Here's the block's code."                                  ← R1 restored, no apology

User: "and with citations? where exactly?"
Agent: → get_block(session, "b3")   (idempotent)
        "Anchored to: TokenBucket.cs:12 (cache declaration), TokenBucket.cs:13 (Allow method)."  ← R2 restored
```

---

# PART 4 — Building the agent: milestones, checks, DoD

Building the **agent** (the `.md` file) proceeds in four increments. Each has an observable check. Here
we list **what must work** at each increment.

| Incr. | Agent content | Check (observable in a real host) |
|---|---|---|
| **A0** | Part 1 (WHAT) + Part 2 (HOW) for F0–F2, Mode A | on a local diff: plan announced; first block co-present; accept/correction recorded |
| **A1** | F3–F4 (peeking, resume) | peeking at a related block doesn't advance; on resume `review_status` realigns |
| **A2** | Full Part 3 (recovery R1–R9) | for each R: if the action is missing, the command restores it via the tool; natural tone |
| **A3** | F5 Mode B + R10 | preview → confirm → post; without confirmation it doesn't post; R10 blocks the premature post |

## 4.1 Agent Definition of Done

- [ ] **WHAT:** all six phases (F0–F5) have behavior and "doesn't" described in the `.md` file.
- [ ] **HOW:** each action maps to a real spec tool and its fields (no invented field; in particular
      uncertainty = `explanation.uncertainty`, not `metadata`).
- [ ] **Recovery:** the **R1–R10** commands are documented in the file with: synonyms, tool, guaranteed
      field, behavior, and tone.
- [ ] **Recovery contract (§3.3):** anchoring, exact rendering, no-apology, idempotence, no-state-change,
      error handling — all present.
- [ ] **Consistency:** no verdict language in the file; every tool cited exists in
      [`spec/mcp-tools.json`](../spec/mcp-tools.json); the guardrails point to [`GUARDRAILS.md`](../GUARDRAILS.md).
- [ ] **Verification:** co-presence / grounding / no-verdict / HITL covered by the **unit tests and
      technical gates** of [`docs/22`](22-roadmap-esecutiva-mvp.md) §0 (`G-SCHEMA`, `G-GROUNDING`,
      `G-NOVERDICT`, `G-RECOVERY`). The `evals/` suite (gate `G-EVAL`) returns once the MVP is complete.

## 4.2 How to test recovery (protocol)

For each command **R1–R10**, in a real host (Claude Code / Cursor):

1. **Induce the failure** of the instructed action (e.g. ask the agent to "explain without showing the
   code" to test R1). *Alternatively*, just verify the command produces the correct rendering.
2. **Utter the command** (and at least one synonym).
3. **Verify** that: (a) the correct tool was called; (b) the guaranteed field is rendered on screen;
   (c) the tone is natural (no apology); (d) repeating the command gives an identical result (idempotence);
   (e) the state (`accepted`/`corrections`/position) hasn't changed.

**Stop-if:** a command doesn't call the tool, or "compensates" from memory, or alters state, or enters a
loop of apologies.

---

## References

- **Agent definition:** [`agent/reviewcheck.agent.md`](../agent/reviewcheck.agent.md)
- **MCP contracts (truth):** [`spec/mcp-tools.json`](../spec/mcp-tools.json)
- **Enforcement and layers:** [`GUARDRAILS.md`](../GUARDRAILS.md) §2–§3
- **Non-negotiable constraints:** [`docs/13`](13-specifica-build.md) §1

**Status:** agent plan complete (WHAT + HOW + per-action recovery). Ready for increments A0–A3.