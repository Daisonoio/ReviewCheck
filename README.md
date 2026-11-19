# ReviewCheck

**Guided, step-by-step code review that helps you actually understand the code an AI wrote for you — so you can own it, not just approve it.**

[![license](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
![status](https://img.shields.io/badge/status-skill%20in%20design-orange)

> [!IMPORTANT]
> **ReviewCheck is being rebuilt as a portable agent skill.** The skill in
> [`skills/reviewcheck/`](skills/reviewcheck/) is a first draft and not yet validated with users.
> The original implementation — a .NET MCP server, built and tested — is kept unchanged in
> [`V 0.1/`](V%200.1/).

## What it does

When an AI writes a large change, it is tempting to approve it without really understanding it.
ReviewCheck splits the change into **small, ordered blocks** and walks you through them **one at a
time**:

- code and explanation **together**, every statement tied to **real line numbers**;
- context from the **rest of the codebase**: what the change uses, who calls it, which tests cover
  it, why the old code was the way it was;
- for each block, **you** decide: accept, or request a correction with a note;
- the outcome is the **sum of your decisions** — the AI never gives a verdict.

## Why a skill

A skill is a folder of instructions (`SKILL.md`) that AI coding assistants read and follow. The
format is an open standard read by Claude Code, GitHub Copilot, Cursor, and others, so one folder
works everywhere, in any programming language, with no build, no server, and no extra API key.

The v0.1 engine is planned to come back as an **optional download** that makes the fragile parts
deterministic (block splitting, citations, impact analysis, saved progress, posting a PR review).
The skill works without it.

## Repository layout

```
skills/reviewcheck/
  SKILL.md                 the flow and the guardrails
  references/              details the assistant reads only when needed
    segmentation.md        collecting changes, splitting and ordering blocks
    presentation.md        the fixed format of a block
    codebase-context.md    using the rest of the codebase, with limits
    state.md               saving and resuming a review
    pull-request.md        reviewing and closing a GitHub pull request
    engine.md              using the optional engine
  assets/
    state.schema.json      shape of the saved review state
V 0.1/                     the original .NET MCP server, tests, evals, docs (frozen)
```

## Try the draft

Copy `skills/reviewcheck/` into the skills folder your assistant reads, for example
`.claude/skills/` (Claude Code), `.github/skills/` (GitHub Copilot), or `.agents/skills/`. Then, in
a repository with changes, ask: *"review my changes with ReviewCheck"*.

## Roadmap

| Step | Status |
|---|---|
| Skill draft (`SKILL.md` + references) | 🟡 in progress |
| Validation with 4–6 developers against a plain prompt | ⬜ planned |
| Optional engine extracted from `V 0.1/` (CLI + MCP) | ⬜ planned |
| Host adapters (hooks for Claude Code, Cursor, Copilot) | ⬜ planned |

## Security & license

See [`SECURITY.md`](SECURITY.md) to report a vulnerability privately. Licensed under
[MIT](LICENSE).
