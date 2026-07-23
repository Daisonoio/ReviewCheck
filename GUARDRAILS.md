# GUARDRAILS.md — guardrails and oversight

ReviewCheck is, by construction, a tool for **human oversight**: as agents write a growing share of the
code, the scarce resource becomes **human supervision that scales**. This file documents the guardrails
as a **versioned, verifiable capability**, not as good intentions.

## 1. Principle

The typical case: an agent wrote the code; understanding it is how the human keeps **ownership**. So the
AI **explains and assists; the human understands and decides.** No outcome is produced by the AI:
approval or request-changes is the **sum of per-block human decisions**, plus an **explicit
confirmation**.

## 2. The guardrails (what, and how each is enforced)

| # | Guardrail | How it's enforced | Level |
|---|---|---|---|
| G1 | **Co-presence** of code + explanation | The MCP tools return `code` **and** `explanation` together (the schema makes it mandatory); the agent prints them together | Deterministic (data) + instructed (rendering) |
| G2 | **Grounding**: every explanation anchored to ≥1 line citation | The `explanation.citations` field is **mandatory** in the schema; computed from the graph, not invented | **Deterministic** |
| G3 | **Declared uncertainty** when anchoring is weak | The `explanation.uncertainty` field is populated by the deterministic layer; the agent must show it | Deterministic (data) + instructed (rendering) |
| G4 | **No verdict** ("correct/safe/approved") | **No** verdict/auto-approval tool exists (`forbidden_tools`); explicit instruction to the agent | **Deterministic** (absence of tool) + instructed |
| G5 | **Human decision per block** | The outcome is computed only from the `status` set by `accept_block`/`request_correction` | **Deterministic** |
| G6 | **Explicit confirmation before posting** | `submit_review` posts only with `confirm=true`; without it, it's preview only; it fails if any block is undecided | **Deterministic** |
| G7 | **Local / no phone-home** | No network beyond the platform (user token) and the configured LLM; network test in CI | **Deterministic** |

## 3. Enforcement model (intellectual honesty)

We distinguish two levels, because over-claiming "guarantees" would be a broken guardrail in itself:

- **Deterministically guaranteed** — what depends on **our code**: the shape of the data (co-presence
  and mandatory citations in the schema), the absence of verdict tools, the "outcome = human decisions +
  confirmation" rule, no-phone-home. It's **testable** and covered by the **technical gates** in CI
  (see [`docs/22`](docs/22-mvp-execution-roadmap.md) §0 and §4).
- **Strongly instructed** — what, when ReviewCheck runs as an **`.md` agent** inside a host LLM, depends
  on the host: the on-screen *presentation* (visual co-presence, the "explain and ask" tone, the
  one-block-at-a-time rhythm). The LLM almost always follows it, but it's **not an ironclad guarantee**.

**Design consequence:** the substance guardrails (G2, G4, G5, G6, G7) are **deterministic** and hold
regardless of the host; the *presentation* ones (part of G1/G3) are **instructed**. Anyone who wants
full guarantees on presentation too will use the "standalone program" variant (roadmap), where the
interface is ours.

## 4. Oversight signals (local, opt-in)

To "instrument the agent's health" without betraying privacy (no remote telemetry): signals computed
**locally** and shown **on request**, e.g.

- % of explanations **without** a citation (should be 0 → violates G2);
- occurrences of forbidden evaluative language (violates G4);
- ratio of **corrections vs acceptances** (a signal of PR quality, not of the human's performance);
- blocks left **undecided** at `submit_review`.

Guardrail on the metrics themselves: **never** measure "time in the tool", streaks, or reviewer
performance — that would be surveillance and a dark pattern.

## 5. How they're verified

The guardrails aren't claims: they're **verified**. For the MVP, the substance guardrails are covered by
**unit tests and technical gates** (`G-SCHEMA`, `G-GROUNDING`, `G-NOVERDICT`, `G-ROUNDTRIP`, `G-NOPHONE`,
`G-E2E`, `G-RECOVERY` — see [`docs/22`](docs/22-mvp-execution-roadmap.md) §0): a block without a
citation, an emitted verdict, an explanation without code, or an outcome without human decisions all
**fail the build**. The dedicated eval suite (`evals/`, gate `G-EVAL`) is reintroduced once the MVP is
complete ([`docs/22`](docs/22-mvp-execution-roadmap.md) §5).