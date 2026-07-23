# 24 — Development plan: **analysis pipeline** (MVP-2)

> **Purpose:** turn a **diff** into **structural blocks** — small, coherent, ordered, with
> **deterministic line citations** and graph relations. It's the **MVP-2** milestone of
> [`docs/22`](22-roadmap-esecutiva-mvp.md): it replaces the stub's **fixtures**
> ([`docs/23`](23-piano-mcp-stub-first.md)) with **real blocks** extracted from the user's code.
>
> **Important boundary:** this pipeline produces the **structure** (code + citations + relations +
> structural uncertainty). The *narrative* of the explanation (`what/why/link`) is added by the **LLM**,
> which is the next plan ([#3](25-piano-llm.md) — `ReviewCheck.Llm`). Here there's **no LLM**: everything
> is **deterministic** and testable with golden tests.
>
> **Sources of truth:** types in [`ReviewCheck.Core`](../src/ReviewCheck.Core) (`Block`,
> `BlockRelation`, `InteractionPoint`, `Intent`, `Citation`), diff reader in
> [`ReviewCheck.Platform`](../src/ReviewCheck.Platform) (`LocalDiffResult`, `DiffHunk`).

---

## 0. Where the pipeline sits in the picture

```
Platform (diff)  →  PIPELINE (this plan)  →  Llm (plan #3)  →  AnalyzedReview  →  MCP tools
  LocalDiffResult    StructuralBlock[]        Block[]           (seam doc 23)
  (hunk, file)       (code+citations+rel.)    (+ what/why/link)
```

- **Input:** `LocalDiffResult` (already produced by `LocalDiffReader.Read` — it exists and is real).
- **Output:** a deterministic structure that the LLM will complete. We call it **`StructuralBlock`**:
  it's a `Block` **without** the `what/why/link` narrative, but **with** the **citations** (grounding)
  and the graph facts the LLM will have to anchor to already in place.

> **Why grounding is born HERE and not in the LLM.** [`GUARDRAILS.md`](../GUARDRAILS.md) G2 says
> citations are *"computed from the graph, not invented"*. So the **citations are produced by the
> pipeline** (deterministic, from the lines the block covers); the LLM **cannot invent them** because
> they arrive already fixed. The LLM only adds the language, anchored to immutable citations.

### 0.1 The boundary type

```csharp
// ReviewCheck.Pipeline output — a Block missing only the LLM narrative
public sealed record StructuralBlock(
    string Id,
    string Title,                        // deterministic candidate label (the LLM can refine it)
    Intent Intent,                       // heuristic: Definition | Wiring | Usage | Test | ...
    string Code,                         // the block's lines (hunk + minimal context)
    IReadOnlyList<Citation> Citations,   // DETERMINISTIC, ≥1 — the lines the block covers
    IReadOnlyList<string> RelatedBlockIds,
    IReadOnlyList<string> StructuralFacts,   // verifiable facts for the LLM (symbols defined/used, calls)
    string? UncertaintyStructural);          // where the graph doesn't resolve (e.g. external symbol)

public sealed record PipelineResult(
    string Title,
    IReadOnlyList<StructuralBlock> Blocks,        // already ordered (order = index)
    IReadOnlyList<BlockRelation> Relations,
    IReadOnlyList<InteractionPoint> InteractionPoints);  // seams from the graph
```

Plan #3 (LLM) maps `StructuralBlock → Block` by adding `Explanation.{What,Why,Link}` and merging
`Uncertainty`. The `Citations` **pass through untouched**.

---

## 1. The pipeline's non-negotiable principles

1. **Deterministic.** Same diff → same output. No randomness, no LLM. → **golden tests**.
2. **Grounding here.** Every `StructuralBlock` has `Citations` ≥1 pointing to real lines of the block.
3. **Small blocks.** A block is a **unit of intent**, not a file. Invariant: **no block =
   whole file** (except a tiny file). Size budget (SP3).
4. **Deterministic base + fallback.** If Roslyn doesn't resolve (parse failed, non-C# file), it
   **degrades** to per-hunk blocks with minimal structure — **never a crash**.
5. **Declared uncertainty.** Where the graph doesn't resolve a symbol (e.g. a type from an external
   assembly, `dynamic`, reflection), populate `UncertaintyStructural` instead of guessing.

---

## 2. The pipeline stages

The pipeline is a chain of pure stages, each testable in isolation.

### P1 — Structural analysis (Roslyn)
- **What:** parse the **changed files** and map each `DiffHunk` to the **symbols that contain it**
  (method / class / property).
- **MVP scope:** **C#** (Roslyn's native language); other languages later (post-MVP).
- **How:** `Microsoft.CodeAnalysis.CSharp` — build a `SyntaxTree` for each file and, via the
  `SemanticModel`, find the symbol enclosing `newStart..newStart+newCount`.
- **Output:** `hunk → {symbolName, symbolKind, range}`.

### P2 — Dependency graph
- **What:** edges between symbols: **defines / uses / calls** (later mapped to `BlockRelation.RelationType`).
- **Roslyn advantage:** unlike a plain parser, Roslyn has a **semantic model** with **cross-file symbol
  resolution** (via `Compilation`/`SemanticModel`): an `IMethodSymbol` or `ITypeSymbol` is resolved even
  if defined in another file of the project. The cross-file graph in the MVP is therefore **realistic**,
  not a name-based heuristic.
- **Honest limit:** what Roslyn can't resolve without the full project (types from unreferenced external
  assemblies, `dynamic`, reflection) is marked **uncertain** (P7), not guessed.
- **Output:** a list of symbol→symbol edges.

### P3 — Block segmentation
- **What:** group hunks/symbols into **units of intent** (the `StructuralBlock`s).
- **v1 heuristics (deterministic):**
  - **one changed symbol = one block** (base case);
  - **config/wiring** hunks without a symbol (e.g. `Program.cs`, `appsettings.json`) → a block per intent;
  - **size budget**: if a block exceeds the budget (lines/symbols), **flag it**
    (high `EstimatedMinutes` / a note) — in v1 we don't split inside a symbol, but we warn (SP3);
  - **invariant:** no block covers a whole file (except a trivial file).
- **Output:** `StructuralBlock[]` (still unordered).

### P4 — Reading order
- **What:** order the blocks so that **definitions precede uses**.
- **How:** **topological** sort over the block graph; **deterministic tie-break** (by file, then by
  line) so the output is **stable**. **Deterministic fallback** (file/line order) if the graph has
  cycles or is incomplete.
- **Output:** ordered `StructuralBlock[]` (the index becomes `OrderIndex`).

### P5 — Deterministic citations (the grounding)
- **What:** for each block, derive `Citations: {File, Lines}[]` from the lines the block **covers**
  (from the hunks/symbols), in the format `"12"` or `"12-15"`.
- **Guarantee:** **≥1 citation per block** (otherwise the block is invalid → a build error in the
  fixture/golden, not a truncated block at runtime).

### P6 — Relations and seams
- **Relations:** map the graph edges (P2) at the **block** level → `BlockRelation` +
  `RelatedBlockIds` on each `StructuralBlock`.
- **Seams (InteractionPoints):** from the graph's **junctions** (e.g. "block B3 uses what B1
  defines"), generate `InteractionPoint {Text, BlockIds}` — **attention pointers, not verdicts**.
- **Rule:** seams only from the graph; empty graph → **no invented seam**.

### P7 — Structural uncertainty
- **What:** where P1/P2 don't resolve (type from an external assembly, `dynamic`, reflection, partial
  parse), populate `UncertaintyStructural`.
- **Boundary with the LLM:** this is the *structural* uncertainty; the LLM may add *semantic* ones. The
  two merge into `Explanation.Uncertainty` in plan #3.

### P8 — Graceful degradation
- **What:** non-C# file / failed parse → **per-hunk fallback**: each hunk becomes a block with `Code`
  + citations (from the hunk's lines) + `Intent.Other` + `UncertaintyStructural` declaring "structural
  analysis unavailable for this file". **No crash.**
- **Why it matters:** it guarantees the pipeline **always** produces valid blocks, even on code we can't
  analyze → the MCP tools and the agent work anyway.

---

## 3. Technical choices

| Topic | MVP choice | Notes |
|---|---|---|
| Parser/analyzer | **Roslyn** (`Microsoft.CodeAnalysis.CSharp`) | native semantic model, real cross-file |
| Languages | **C#** in v1 | other languages post-MVP (P1/P2 extensible per language) |
| Cross-file graph | resolution via `SemanticModel` | reliable within the project; uncertain beyond |
| Determinism | no LLM, stable tie-break | enables **golden tests** |
| LLM boundary | `StructuralBlock` (without `What/Why/Link`) | plan #3 completes it |

---

## 4. Spikes to close before/while building

| Spike | Question | Timebox | What it unblocks |
|---|---|---|---|
| **SP1** | how much project context does Roslyn need for a useful `SemanticModel` on the diff alone (without a full build)? | 3d | the shape of P2; the `Compilation` loading strategy |
| **SP3** | which block **size budget** holds up working memory? | 2d | the P3 threshold and when to warn |

> If SP1 shows a full build is needed for full semantics, the MVP still holds: use the available partial
> resolution and declare the uncertainty (P7). The value to the user does not depend on the graph being
> complete.

---

## 5. Task sequence (each with a golden check)

Each stage is tested with a **golden test** (xUnit): fixed input files → expected output (snapshot). Deterministic.

| # | Task | Check (green) | Stop-if |
|---|---|---|---|
| **T1** | Load Roslyn `SyntaxTree`/`Compilation`; parse a sample C# file | the sample's AST has the expected nodes | the parser doesn't load the file |
| **T2** | P1: `DiffHunk` → containing symbol (C#) | golden: sample hunk → expected `{symbol, kind, range}` | a hunk inside a method not mapped |
| **T3** | P2: defines/uses/calls graph from the `SemanticModel` | golden: expected edges; unresolved symbol → marked uncertain, **no crash** | crash on an external symbol |
| **T4** | P3: segmentation into `StructuralBlock` | golden: N expected blocks; **no block = whole file**; budget respected/flagged | "one file = one block" |
| **T5** | P4: ordering | golden: definition precedes use; **stable** output across two runs | non-deterministic order |
| **T6** | P5: citations | golden: every block has `Citations` ≥1 pointing to real lines | a block without citations |
| **T7** | P6: relations + seams | golden: expected `RelatedBlockIds` and `InteractionPoints`; empty graph → zero seams | a seam not anchored to the graph |
| **T8** | P7: structural uncertainty | golden: external symbol → `UncertaintyStructural` populated | uncertainty silenced |
| **T9** | P8: degradation (non-C# file) | unsupported file → valid per-hunk blocks, uncertainty declared, **no crash** | crash or invalid blocks |
| **T10** | Orchestration: `AnalysisPipeline.Run(LocalDiffResult) → PipelineResult` | e2e on a sample diff: complete, deterministic `PipelineResult` | non-reproducible output |

---

## 6. MVP-2 technical gates (from `docs/22`)

- **G-NOPHONE** — the pipeline makes **no** network calls (only local static analysis; it does not run the code).
- **G-E2E(real blocks)** — given a sample diff, `PipelineResult` has ordered blocks, with citations to
  real lines, relations, and seams; **no block = whole file**.
- **Determinism** — two runs on the same diff produce identical output (stable golden).
- **No-crash** — on a non-C# file / partial parse, graceful degradation (P8), never an exception.

> Note: the **G-GROUNDING** gate (the LLM rejects output without citations) belongs to MVP-3; here
> grounding is guaranteed *upstream* because the citations are deterministic (P5).

---

## 7. How it plugs in (replaces the stub)

In `ReviewCheck.Mcp` (doc 23) you write `PipelineProvider : IReviewProvider`:

```csharp
public async Task<AnalyzedReview> AnalyzeAsync(Source source)
{
    var diff       = _diffReader.Read(source.Ref);   // ReviewCheck.Platform (exists)
    var structural = _pipeline.Run(diff);            // THIS PLAN → PipelineResult
    var blocks     = await _llm.ExplainAsync(structural.Blocks);  // plan #3 → complete Block[]
    return new AnalyzedReview(structural.Title, blocks, structural.InteractionPoints);
}
```

Then in the composition (DI): `StubProvider` → `PipelineProvider`. **Tools, store, and agent don't
change.** The MVP-1 e2e/recovery tests become regression tests: if they now pass with **real** blocks,
the plug-in worked.

---

## 8. Definition of Done — MVP-2

- [ ] `AnalysisPipeline.Run(LocalDiffResult)` returns a deterministic `PipelineResult`.
- [ ] **P1–P8** implemented for **C#** (Roslyn), each with a green golden test.
- [ ] Every `StructuralBlock` has `Code` + `Citations` (≥1, real lines) + `RelatedBlockIds` +
      `UncertaintyStructural` (nullable).
- [ ] **No block = whole file**; stable order (definitions before uses).
- [ ] **Graceful degradation** on non-C# files (per-hunk fallback, no crash).
- [ ] Gates **G-NOPHONE, G-E2E(real), determinism, no-crash** green.
- [ ] `PipelineProvider` plugs the pipeline into the MCP server; the MVP-1 tests pass with real blocks.

---

## References

- **MVP roadmap:** [`docs/22`](22-roadmap-esecutiva-mvp.md) (MVP-2)
- **Stub-first MCP plan (the `IReviewProvider` seam):** [`docs/23`](23-piano-mcp-stub-first.md)
- **LLM plan (completes the `StructuralBlock`s):** [`docs/25`](25-piano-llm.md)
- **Diff reader (input):** [`ReviewCheck.Platform`](../src/ReviewCheck.Platform)
- **Types:** [`ReviewCheck.Core`](../src/ReviewCheck.Core)

**Status:** MVP-2 plan ready. Next plan: **LLM adapter + prompt/grounding** (completes the blocks).