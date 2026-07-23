# 25 — Development plan: **LLM adapter + grounded explanations** (MVP-3)

> **Purpose:** complete the pipeline's **`StructuralBlock`s** ([`docs/24`](24-pipeline-plan.md)) by
> turning them into real **`Block`s** — adding the `what/why/link` **narrative** with the user's LLM.
> It's the **MVP-3** milestone of [`docs/22`](22-mvp-execution-roadmap.md): it's **the only point in
> ReviewCheck where the AI enters**, and it must be **reined in**.
>
> **Boundary (from the previous plan):** the pipeline has already produced `code` + **deterministic
> citations** + graph facts. The LLM **does not touch the citations** (they're already fixed): it only
> adds language anchored to them. If it errs or is unavailable, it **degrades to facts** — a valid
> `Block` **always** comes out.
>
> **Sources of truth:** the `Explanation`/`Block` records in [`ReviewCheck.Core`](../src/ReviewCheck.Core),
> `StructuralBlock` in [`docs/24`](24-pipeline-plan.md). LLM via the **Anthropic API** (`HttpClient` client).

---

## 0. Where the LLM sits in the picture

```
Platform (diff) → Pipeline (#2) → LLM (THIS PLAN) → AnalyzedReview → MCP tools (#1)
                   StructuralBlock[]  Block[]
                   code + citations   + explanation.{what,why,link}
                   (fixed)            + uncertainty (merged)
```

- **Input:** `StructuralBlock[]` — with `Code`, `Citations` (≥1, **deterministic**), `StructuralFacts`,
  `UncertaintyStructural`.
- **Output:** `Block[]` — the same blocks with a complete, valid `Explanation` (`Block` record).
- **The LLM produces ONLY** `What`, `Why`, `Link`. Everything else is **stapled** from the pipeline.

---

## 1. Non-negotiable principles (the reins)

1. **The LLM narrates, it doesn't anchor.** The `Citations` come from the pipeline and are **stapled
   verbatim** onto the `Block`. The LLM **neither generates nor modifies them** → it cannot invent
   evidence ([`GUARDRAILS.md`](../GUARDRAILS.md) G2).
2. **No verdict.** Never "correct / safe / approved / it's a bug". **Describe and ask** (G4). Validation
   **rejects** evaluative output.
3. **Declared uncertainty.** The pipeline's `UncertaintyStructural` is **always** carried over; the LLM
   may add *semantic* uncertainty. The two merge into `Explanation.Uncertainty` (G3).
4. **Degradation to facts.** LLM down / invalid output after retry → `Block` built from the
   `StructuralFacts` (deterministic), with `Uncertainty` declaring "explanation not generated".
   **A valid `Block` always comes out.**
5. **The user's LLM, not ours.** The code goes **only** to the LLM the user configures (host or their own
   key); **never** to a ReviewCheck server (G7, no phone-home). See §6.

---

## 2. Architecture

### 2.1 The model seam (host-or-BYO)

```csharp
// one interface, two implementations
public interface ILlmProvider
{
    Task<string> CompleteAsync(string system, string user, CancellationToken ct = default);
}

// MVP: the user's key, via the Anthropic API (typed HttpClient)
public sealed class AnthropicByoProvider : ILlmProvider { /* uses the user's key */ }

// LATER (same seam): uses the host agent's LLM via MCP sampling
public sealed class HostSamplingProvider : ILlmProvider { /* doesn't block the MVP */ }
```

> **Locked decision:** **BYO key first**; host/sampling is an addition behind the **same** interface —
> don't get stuck on it.

### 2.2 The adapter

```csharp
public sealed class LlmAdapter
{
    public LlmAdapter(ILlmProvider provider) { /* ... */ }

    // turns structural blocks into complete, VALID Blocks
    public Task<IReadOnlyList<Block>> ExplainAsync(IReadOnlyList<StructuralBlock> blocks);
}
```

`ExplainAsync()` processes **one block at a time** (independent explanations, targeted context): prompt →
`provider.CompleteAsync` → parse → validate → build `Block` → (if needed) degrade.

---

## 3. The per-block flow

```
StructuralBlock
   │  1. build the prompt (system + user with the code, the fixed citations, the StructuralFacts)
   ▼
provider.CompleteAsync()  → text (JSON: {what, why, link, uncertainty_semantic?})
   │  2. parse the JSON
   ▼
validation (rubric §5)
   │  ok? ──────────────► 3. BUILD Block:
   │                          Code        = StructuralBlock.Code        (verbatim)
   │                          Citations   = StructuralBlock.Citations   (verbatim, NEVER from the LLM)
   │                          What/Why/Link = from the LLM
   │                          Uncertainty = merge(structural, semantic)
   │                          → BlockGuard.Ensure (must pass)
   │
   │  failed? → retry ONCE (stricter prompt)
   │              └─ still failing? ──► 4. DEGRADE TO FACTS:
   │                                         What/Why = from StructuralFacts (deterministic)
   │                                         Uncertainty = "LLM explanation unavailable"
   ▼
valid Block (always)
```

**Crucial point:** `Citations` and `Code` **never** pass from the LLM to the `Block`. They're taken from
the pipeline. This way the `Block` record's invariant (citations ≥1) is guaranteed **by construction**,
not by luck.

---

## 4. Prompt design

### System (fixed, the rules)
- Role: *"you explain the code to make it understood; you don't judge it, you don't approve it"*.
- Constraints: **no verdicts** (no "correct/safe/approved/it's a bug"); **cite only the provided lines**;
  **use only the provided relations** (don't invent any); **declare uncertainty**; **describe and ask**.
- Format: reply in **JSON** `{ what, why, link, uncertainty_semantic }`.

### User (per block, the facts)
- The block's **code**.
- The **fixed citations** (the lines to anchor to).
- The **StructuralFacts** (symbols defined/used, calls) and the available **relations**.
- Any **`UncertaintyStructural`**.

### What we ask for
- `what`: 1–2 sentences, *what* the block does.
- `why`: 1–2 sentences, *why* it's needed / what it connects to.
- `link`: connections **only** from the provided facts/relations.
- `uncertainty_semantic`: where the model itself is uncertain (or `null`).

---

## 5. Validation (the rubric = the bouncer)

Every LLM output passes these checks **before** becoming a `Block`:

| Check | Rule | If it fails |
|---|---|---|
| **Presence** | `what` and `why` non-empty/non-trivial | retry → degrade |
| **No-verdict** | no evaluative language about correctness/safety ("correct", "safe", "approved", "it's a bug/vulnerability" as a judgment) | retry → degrade |
| **Anchored link** | `link` references **only** the provided `RelatedBlockIds`/`StructuralFacts` | retry → degrade |
| **No hallucinations** | does not cite files/symbols absent from the block and the facts | retry → degrade |
| **Uncertainty** | if `UncertaintyStructural` is present, it must appear in `Explanation.Uncertainty` | automatic merge |

- **Retry only once**, with a system prompt that reiterates the violated rule.
- **Then degradation to facts** (§3, step 4): never block the review because of the LLM.

> Note: **no-verdict** is also guaranteed upstream (no verdict tool exists, G-NOVERDICT); here it's a
> second filter on the *language* of the explanation.

---

## 6. Data flow

This is **the only stage that sends the code to a model**. It must be made explicit, because it's both a
constraint (G7) and a **selling point**:

- **BYO key:** the block's code goes to the **user's Anthropic account** (their key), not ours.
- **Host/sampling (later):** it goes to the **host agent's** model (Claude Code/Cursor), i.e. still the
  LLM the user already uses.
- **In both cases:** no ReviewCheck server in the middle, **no phone-home**. The code doesn't leave the
  user's perimeter → the configured LLM.
- It's sent **one block at a time**, minimal context: reduced data surface.

---

## 7. Test strategy

The LLM **is not deterministic** → no golden tests on this stage. You test the adapter's **mechanics**
with a **fake provider**, and **quality** with a rubric (post-MVP, when `evals/` returns).

- **`FakeLlmProvider`**: returns predefined outputs (valid, invalid, with a verdict, with an invented
  link) to test parse/validation/construction/degradation **deterministically**.
- **Key unit tests (xUnit):**
  - valid output → `Block` that passes `BlockGuard`; `Citations` identical to the pipeline's;
  - output without `what/why` → **degrades to facts**, `Block` still valid (**G-GROUNDING**);
  - output with a verdict → **rejected**, then degraded (**G-NOVERDICT** on language);
  - output with an invented link → rejected;
  - `UncertaintyStructural` present → appears in the output.
- **Quality on a real LLM** (accuracy, usefulness, zero hallucinations): **post-MVP rubric** (spike SP2 for
  a first signal).

---

## 8. Spike to close

| Spike | Question | Timebox |
|---|---|---|
| **SP2** | quality of grounded explanations on real PRs, with a BYO key? | 3d |

> If SP2 shows weak explanations, the MVP **still holds**: grounding (citations) is deterministic and
> degradation to facts always guarantees an honest `Block`. The quality of the *narrative* is iterated on
> the prompt.

---

## 9. Task sequence (each with a check)

| # | Task | Check (green) | Stop-if |
|---|---|---|---|
| **T1** | `ILlmProvider` + `AnthropicByoProvider` (key from config/env, model choice) | with a key, returns a completion; without a key, a clear error | the key ends up in the logs |
| **T2** | `FakeLlmProvider` (for tests) | returns predefined outputs | — |
| **T3** | Prompt builder (system + user per block) | the prompt contains code, fixed citations, facts; no invitation to a verdict | the prompt asks for a judgment |
| **T4** | JSON output parser | valid JSON → object; broken JSON → handled error (→ retry/degrade) | crash on malformed output |
| **T5** | `Block` builder (staple citations+code, add what/why/link, merge uncertainty) | `Block` passes `BlockGuard`; `Citations` == pipeline (verbatim) | citations taken from the LLM |
| **T6** | Rubric validation (§5) + retry-once | verdict/invented-link/empty → rejected | an evaluative output passes |
| **T7** | Degradation to facts | LLM down / invalid twice → `Block` valid from `StructuralFacts` | the review stalls on an LLM error |
| **T8** | `LlmAdapter.ExplainAsync(StructuralBlock[]) → Block[]` + plug-in to `PipelineProvider` | e2e: real `StructuralBlock`s → complete, co-present `Block`s | a `Block` without an explanation |

---

## 10. MVP-3 technical gates (from `docs/22`)

- **G-GROUNDING** — every emitted `Block` has `Citations` ≥1 (guaranteed by construction: stapled from
  the pipeline); the adapter **never** emits a block without an explanation (it degrades). Dedicated unit test.
- **G-NOVERDICT** — validation rejects verdict language; no verdict tool exists.
- **No phone-home** (G7) — the code goes only to the user's configured LLM (§6); a network test confirms
  no other destination.

---

## 11. How it plugs in (closes the MVP core)

In `PipelineProvider.AnalyzeAsync()` (doc 24 §7):

```csharp
var structural = _pipeline.Run(diff);                        // #2
var blocks     = await _llm.ExplainAsync(structural.Blocks); // THIS PLAN → complete Block[]
return new AnalyzedReview(structural.Title, blocks, structural.InteractionPoints); // seam doc 23
```

With this, `get_review_plan` returns **real blocks with grounded explanations**: the MVP demo (local
diff → a real guided review) is complete. The MVP-1 e2e/recovery tests now run over the entire real chain.

---

## 12. Definition of Done — MVP-3

- [ ] `ILlmProvider` with a working `AnthropicByoProvider` (BYO key); `HostSamplingProvider` behind the
      same interface (even as a stub).
- [ ] `LlmAdapter.ExplainAsync` produces `Block[]` that **always** pass `BlockGuard`.
- [ ] **Citations and code** taken **verbatim** from the pipeline; the LLM only adds `what/why/link`.
- [ ] **Rubric validation** (no-verdict, anchored link, no-hallucinations, uncertainty) + **retry** +
      **degradation to facts**.
- [ ] Key **never logged**; code sent **only** to the user's LLM (no phone-home).
- [ ] Gates **G-GROUNDING, G-NOVERDICT, G7** green (unit tests with `FakeLlmProvider`).
- [ ] Plugged into `PipelineProvider`: the real chain runs end-to-end.

---

## References

- **MVP roadmap:** [`docs/22`](22-mvp-execution-roadmap.md) (MVP-3)
- **Pipeline (produces the `StructuralBlock`s):** [`docs/24`](24-pipeline-plan.md)
- **Stub-first MCP (the `IReviewProvider` seam):** [`docs/23`](23-mcp-stub-first-plan.md)
- **Guardrails (grounding, no-verdict, no phone-home):** [`GUARDRAILS.md`](../GUARDRAILS.md) G2/G3/G4/G7
- **Types:** [`ReviewCheck.Core`](../src/ReviewCheck.Core)

**Status:** MVP-3 plan ready. With #1+#2+#3 the **MVP core is fully planned**; the next high-leverage work
is **building it** (not more plans) and packaging the demo.