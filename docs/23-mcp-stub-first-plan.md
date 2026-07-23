# 23 — Development plan: **stub-first MCP server** (MVP-1)

> **Purpose:** build `ReviewCheck.Mcp` as an **end-to-end skeleton runnable in Claude Code**, with the 7
> tools returning **fixture blocks** — fake but **schema-conformant** (`code`, `citations` ≥1,
> `uncertainty`). It's the **MVP-1** milestone of [`docs/22`](22-mvp-execution-roadmap.md): it unblocks
> everything else and makes the **R1–R10** recovery commands of [`docs/21`](21-development-plan.md)
> **testable right away**.
>
> **Sources of truth:** [`spec/mcp-tools.json`](../spec/mcp-tools.json) (tool contracts),
> [`spec/session-state.schema.json`](../spec/session-state.schema.json) (local state), C# types in
> [`ReviewCheck.Core`](../src/ReviewCheck.Core). In case of conflict, the spec wins.

---

## 0. Core idea: the **seam** that makes stub-first possible

The MCP server must not *know* whether the blocks come from fixtures or from the real pipeline. We
insert a single interface — **`IReviewProvider`** — that both implement:

```
                       ┌─────────────────────────────┐
  Claude Code  ──MCP──▶│  ReviewCheck.Mcp (7 tools)  │
  (.md agent)          │  + SessionStore (state)     │
                       └──────────────┬──────────────┘
                                      │ calls
                                      ▼
                          interface IReviewProvider
                          AnalyzeAsync(source) → AnalyzedReview
                             ▲                    ▲
                   ┌─────────┘                    └───────────┐
            StubProvider (MVP-1)            PipelineProvider (MVP-2/3)
            returns fixtures                Roslyn + graph + LLM
```

**Consequence:** in MVP-1 we wire `StubProvider`. In MVP-2/3 we replace **only** the provider
implementation — the 7 tools, the session store, and the agent **don't change**. The seam is
`AnalyzeAsync()`.

**Enforcement point (key to recovery):** every tool that returns a block passes it through
`BlockGuard.Ensure()` from `ReviewCheck.Core` (which validates the `Block` record's invariant). This
way **even the fixtures** are forced to have `code` + `explanation.citations` (≥1) + an `uncertainty`
field. This is what makes R1–R10 verifiable against the stubs already: `get_block` *cannot* return a
block without co-presence/grounding.

---

## 1. Architecture and file layout

```
src/ReviewCheck.Mcp/
├─ Program.cs               # entry: builds the MCP host, registers the tools, connects stdio
├─ McpServerSetup.cs        # BuildServer(provider, store) → registers the 7 tools
├─ BlockGuard.cs            # Ensure(): validates the Block record — single validation choke point
├─ Provider/
│  ├─ IReviewProvider.cs    # interface IReviewProvider + record AnalyzedReview
│  ├─ StubProvider.cs       # StubProvider — returns fixtures
│  └─ Fixtures/
│     └─ SampleCSharp.cs    # ≥1 sample analyzed review (schema-conformant Blocks)
└─ Tools/                   # one file per tool (or a single handler if you prefer it more compact)
   ├─ GetReviewPlanTool.cs
   ├─ NextBlockTool.cs
   ├─ GetBlockTool.cs
   ├─ AcceptBlockTool.cs
   ├─ RequestCorrectionTool.cs
   ├─ ReviewStatusTool.cs
   └─ SubmitReviewTool.cs

src/ReviewCheck.Session/
└─ SessionStore.cs          # SessionStore: Create/Load/Save/Mutate on a JSON file (local schema)
```

### 1.1 The seam's exchange type

```csharp
// Provider/IReviewProvider.cs
using ReviewCheck.Core;

public sealed record AnalyzedReview(
    string Title,
    IReadOnlyList<Block> Blocks,                     // already ordered (order = index)
    IReadOnlyList<InteractionPoint> InteractionPoints,
    int? EstimatedMinutes = null);

public interface IReviewProvider
{
    Task<AnalyzedReview> AnalyzeAsync(Source source);
}
```

`StubProvider.AnalyzeAsync()` ignores `source` and returns a fixture. `PipelineProvider.AnalyzeAsync()`
(later) runs diff→Roslyn→graph→segmentation→LLM. **Same signature.**

---

## 2. The 7 tools: contract, stub behavior, validation

Each tool implements the `inputSchema`/`outputSchema` of [`spec/mcp-tools.json`](../spec/mcp-tools.json),
registered via the **`ModelContextProtocol`** SDK (stdio host). In MVP-1 the behavior is real in the
**session mechanics**; only the *provenance* of the blocks is fake.

| Tool | What it does (MVP-1) | Validation |
|---|---|---|
| `get_review_plan` | `provider.AnalyzeAsync(source)` → creates a session (all blocks `status=pending`, `current=blocks[0]`) → returns the plan + `first_block` | `BlockGuard.Ensure(first_block)` |
| `next_block` | advances the pointer to the next by `order_index`; returns block + `position` + `progress_pct` | `BlockGuard.Ensure(block)` |
| `get_block` | returns the block for `block_id` **without** advancing (also used for peeking/recovery) | `BlockGuard.Ensure(block)` |
| `accept_block` | `status[block_id]=accepted`; persists | — |
| `request_correction` | `status[block_id]=correction_requested`, stores `note`, generates `note_id`; persists | — |
| `review_status` | counts `accepted`/`corrections`, `index`/`total`, `progress_pct`, `current_block_id` | — |
| `submit_review` | if any `pending` exist → returns `undecided_blocks` and **does not proceed**; **Mode A**: `posted=false`, `outcome = ready_to_proceed \| corrections_to_apply`, `notes[]` = corrections | — |

### 2.1 Details that are easy to get wrong

- **`get_block` does not move the pointer.** `next_block` does. (Needed for recovery R5 "peek without advancing".)
- **`submit_review` fails with undecided blocks** (deterministic gate G6): it populates `undecided_blocks[]`
  and does not close. The agent must take the user back to those blocks.
- **Mode B is out of the MVP.** If `source.type == "pull_request"`, `submit_review` returns an explicit
  "Mode B not included in the MVP" outcome — **no network, no token**.
- **`progress_pct`** = decided blocks / total × 100 (decided = accepted + correction_requested).
- **No verdict tool** (G-NOVERDICT): do not add `is_correct`, `auto_approve`, etc.

---

## 3. The fixtures (the heart of the stub)

At least **one** realistic analyzed review in `Provider/Fixtures/SampleCSharp.cs`, with **3–4 C#
blocks** that **pass `BlockGuard.Ensure`**. It must cover the cases needed to test recovery:

- at least one block with `explanation.uncertainty` **non-null** (to test R3 "uncertainty?");
- at least one block with `related_block_ids` populated (for R4 "what does it touch?" and R5 "peek");
- `citations` in mixed formats (`"12"` and `"12-15"`);
- `interaction_points` with ≥1 seam referencing real `block_ids`;
- varied `intent` (`Definition`, `Wiring`, `Usage`, `Test`).

> **Rule:** the fixtures are built with the `ReviewCheck.Core` types and validated in a test (`Sample
> passes BlockGuard`). If the record changes tomorrow, the fixture breaks the build → good.

---

## 4. Session store (MVP-1: minimal but file-backed)

`ReviewCheck.Session.SessionStore` — JSON on a local file (via `System.Text.Json`), conforming to
[`session-state.schema.json`](../spec/session-state.schema.json):

```csharp
public sealed class SessionStore
{
    public string Create(AnalyzedReview analyzed, Source source);   // → session id; writes the file
    public SessionState Load(string session);
    public void Save(SessionState state);
    public void SetStatus(string session, string blockId, BlockStatus status, string? note = null);
    public (Block Block, Position Position) Advance(string session); // moves current_block_id
}
```

- **Path:** `.reviewcheck/session-<id>.json` (already in `.gitignore`).
- **MVP-1:** a plain file; **hardening deferred** to MVP-4/post (restricted permissions, cleanup,
  encryption — see `docs/22` §5). Implementing it on a file from the start is a few lines and **gives you
  resume for free** (the `G-ROUNDTRIP` gate of MVP-4 becomes almost already passed).
- **No DB, no network** (G-NOPHONE).

---

## 5. Wiring in Claude Code

1. `dotnet build` → the `ReviewCheck.Mcp` executable/DLL.
2. Register the MCP server (stdio) in the host config, e.g.:
   ```json
   { "mcpServers": { "reviewcheck": { "command": "dotnet", "args": ["src/ReviewCheck.Mcp/bin/Debug/net8.0/ReviewCheck.Mcp.dll"] } } }
   ```
   (or, installed as a tool: `{ "command": "reviewcheck-mcp" }`).
3. Load [`agent/reviewcheck.agent.md`](../agent/reviewcheck.agent.md) as an agent/skill.
4. Try it: "review the local diff" → the agent calls `get_review_plan({source:{type:"local"}})` (the stub
   ignores the ref and returns the fixture) → the block-by-block flow starts.

---

## 6. Task sequence (ordered, each with a check)

| # | Task | File | Check (green) | Stop-if |
|---|---|---|---|---|
| **T1** | MCP host + stdio + 1 no-op tool | `Program.cs`, `McpServerSetup.cs` | Claude Code lists the `reviewcheck.*` tool | the server doesn't register |
| **T2** | `IReviewProvider` + `AnalyzedReview` | `Provider/IReviewProvider.cs` | the project compiles | — |
| **T3** | `StubProvider` + fixture | `Provider/StubProvider.cs`, `Fixtures/SampleCSharp.cs` | test: the fixture **passes `BlockGuard`**; ≥1 non-null `uncertainty`; ≥1 `related_block_ids` | a fixture fails the schema |
| **T4** | `SessionStore` on a file | `src/ReviewCheck.Session/SessionStore.cs` | round-trip: Create→Load→Save reconstructs the state | state not reconstructible |
| **T5** | `get_review_plan` | `Tools/GetReviewPlanTool.cs` | e2e: returns `session` + a co-present `first_block`; `BlockGuard.Ensure` doesn't throw | `first_block` without code/citations |
| **T6** | `next_block` + `get_block` | `Tools/NextBlockTool.cs`, `GetBlockTool.cs` | `next_block` advances, `get_block` does **not**; both co-present | `get_block` moves the pointer |
| **T7** | `accept_block` + `request_correction` + `review_status` | respective | the `status` values are recorded; `review_status` counts correctly | the counters don't reflect the decisions |
| **T8** | `submit_review` (Mode A) | `Tools/SubmitReviewTool.cs` | pending → `undecided_blocks`; all accepted → `ready_to_proceed`; ≥1 correction → `corrections_to_apply` + `notes`; `posted=false` | posts something or closes with pending |
| **T9** | Wiring + manual test + **recovery R1–R10** | host config | in Claude Code: co-presence; "show the code"/"with citations"/"uncertainty?" restore via `get_block` | a recovery command doesn't call the tool or "compensates" from memory |

---

## 7. MVP-1 technical gates (from `docs/22`)

- **G-CI** — build + tests green in CI (`dotnet build` / `dotnet test`).
- **G-SCHEMA** — a `Block` without `code` or without `citations` does **not** pass `BlockGuard.Ensure`
  (positive test: the fixture passes; negative test: a truncated block fails).
- **G-E2E(stub)** — the chain `get_review_plan → next_block → accept/correction → submit_review` runs
  end-to-end against the stub and every block comes out **co-present**.
- **G-RECOVERY(stub)** — for each R1–R10 the command restores the action by re-calling the tool
  (protocol [`docs/21`](21-development-plan.md) §4.2), with a natural tone and without altering state.

---

## 8. Going from stub to real (what changes later)

When the pipeline arrives in MVP-2/3:
- You write `PipelineProvider : IReviewProvider` whose `AnalyzeAsync()` calls
  `ReviewCheck.Platform` (diff) → `ReviewCheck.Pipeline` (Roslyn/graph/segmentation) →
  `ReviewCheck.Llm` (grounded explanations).
- In the composition (DI) you replace `StubProvider` with `PipelineProvider`.
- **Nothing else changes:** tools, store, agent, and the e2e/recovery tests stay identical. The very
  MVP-1 tests become the regression tests of MVP-2/3.

---

## 9. Definition of Done — MVP-1

- [ ] `ReviewCheck.Mcp` exposes the **7 tools** of the spec over stdio; Claude Code lists and calls them.
- [ ] `IReviewProvider` is the only seam; `StubProvider` returns fixtures **valid per `BlockGuard`**.
- [ ] Every tool that returns a block passes through `BlockGuard.Ensure` (co-presence + grounding guaranteed).
- [ ] `SessionStore` on a file: Create/Load/Save/round-trip works.
- [ ] End-to-end Mode A flow: plan → blocks → accept/correction → `submit_review`
      (`ready_to_proceed` / `corrections_to_apply`, `posted=false`).
- [ ] **Recovery R1–R10** verified against the stubs in Claude Code.
- [ ] Gates **G-CI, G-SCHEMA, G-E2E(stub), G-RECOVERY(stub)** green.
- [ ] No network (G-NOPHONE) and no verdict tool (G-NOVERDICT).

---

## References

- **MVP roadmap:** [`docs/22`](22-mvp-execution-roadmap.md) (MVP-1)
- **Agent plan (WHAT/HOW/recovery R1–R10):** [`docs/21`](21-development-plan.md)
- **Tool contracts:** [`spec/mcp-tools.json`](../spec/mcp-tools.json)
- **Local state:** [`spec/session-state.schema.json`](../spec/session-state.schema.json)
- **Types/invariants:** [`ReviewCheck.Core`](../src/ReviewCheck.Core)

**Status:** MVP-1 plan ready. Next plan: **pipeline** (Roslyn → graph → segmentation → order).