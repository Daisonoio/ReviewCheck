# ReviewCheck — capability eval suite

An **evaluation suite** for ReviewCheck's agent capabilities. Where the unit tests assert exact,
deterministic outputs of individual functions, the evals assert that the **guardrails** — the
properties that make the tool safe to hand an agent — hold as *capabilities* across a **corpus** of
representative cases, and they **gate CI** so a capability regression fails the build.

## Why a separate suite (the idea)

The agentic part of ReviewCheck (the LLM narration, and the host agent that presents it) is not
deterministic. You can't assert "the explanation is exactly X". You *can* assert the invariants that
must hold **whatever** the model says:

- every block is **co-present** (code + explanation) and **grounded** (real line citations);
- no block emits a **verdict** ("correct / safe / buggy");
- the reading **order** puts definitions before their uses;
- **uncertainty is declared** for symbols defined outside the change;
- and when the model misbehaves, the **guardrail net** catches it: verdicts and hallucinated
  references are rejected, an unavailable model **degrades to facts**, and citations are stapled
  **verbatim** from the pipeline (the model narrates, it never anchors).

Those are *capabilities*, and this suite measures them. It is the difference between *"the guardrails
are documented"* and *"the guardrails are measured and enforced on every PR"*.

## The two tiers

| Tier | Input | What it proves | Determinism |
|---|---|---|---|
| **Structural** | the corpus run through the **real pipeline** + the facts narrator | co-presence, grounding-real, no-verdict, reading-order, declared-uncertainty | offline, no key |
| **LLM-behaviour** | one real block + a **scripted model** emitting bad output | verdict-rejected, hallucination-rejected, degrade-on-unavailable, citations-verbatim | offline, scripted |

Both tiers are **fully offline and reproducible** — no network, no API key, no randomness. (A third,
*model-quality* tier — grading a real model's prose with a live key — is intentionally **not** a CI
gate, because it is probabilistic; it belongs in a separate, non-blocking run.)

A design note worth calling out: the checks judge **independently** of the implementation. The eval
uses its *own* verdict word list, not `ExplanationRubric`'s regex — so a bug in the rubric can't hide
from the eval that is supposed to catch it.

## The corpus

`corpus/<case>/*.cs` — each subfolder is a case; each `.cs` file is treated as a brand-new file (the
whole file is the change). That mirrors how ReviewCheck is actually used — an agent adds a feature
across new files — and needs no git. The corpus is **versioned data**: grow a capability's coverage by
dropping in another case, no code change required.

Current cases: `discount-pricing` (a cross-file chain → edges, order, seams), `config-toggle` (a
trivial single block), `external-dependency` (a call to a symbol outside the change → uncertainty).

## Run it

```bash
dotnet run --project eval/ReviewCheck.Evals/ReviewCheck.Evals.csproj -c Release
```

It prints a **scorecard**, writes `eval-report.json`, and exits non-zero if any capability is below
100% (these are safety guarantees — one failure is a regression). CI runs it as the `capability-evals`
job and uploads the scorecard as an artifact.

```
CAPABILITY                       PASS    RATE  STATUS
co-presence                       3/3  100 %  PASS
grounding-real                    3/3  100 %  PASS
no-verdict                        3/3  100 %  PASS
reading-order                     1/1  100 %  PASS
declared-uncertainty              1/1  100 %  PASS
llm:verdict-rejected              1/1  100 %  PASS
llm:hallucination-rejected        1/1  100 %  PASS
llm:degrade-on-unavailable        1/1  100 %  PASS
llm:citations-verbatim            1/1  100 %  PASS
```

## Files

- `EvalCorpus.cs` — loads the corpus, turns each source into an "added file" diff.
- `Capabilities.cs` — the checks (Tier A structural, Tier B LLM-behaviour).
- `ScriptedLlmProvider.cs` — the scripted model for the LLM-behaviour tier.
- `Scorecard.cs` — aggregation, rendering, JSON report.
- `Program.cs` — the runner + the CI exit code.
