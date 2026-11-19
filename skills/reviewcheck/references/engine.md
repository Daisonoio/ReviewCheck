# Using the ReviewCheck engine (optional)

The engine is an optional, downloadable binary that makes the fragile parts deterministic:
splitting into blocks, citations, reading order, impact analysis, state, and posting. The skill
works without it; with it, the same diff always gives the same plan.

**Status: planned.** The engine is being extracted from the v0.1 .NET implementation
(`V 0.1/` in this repository). Until a release exists, `reviewcheck --version` fails and the
skill uses the base flow.

## Planned contract

One binary, two faces:

- **CLI** (works in any assistant that can run shell commands):
  - `reviewcheck plan --json [--ref <ref>]` — blocks, order, citations, seams;
  - `reviewcheck context --block <id> --json` — definitions, callers, tests for one block;
  - `reviewcheck state <get|decide|pause|close> --json` — the review state;
  - `reviewcheck post --pr <n> --confirm-token <token>` — posts one review from the state.
- **MCP** (`reviewcheck mcp`) — the same operations as typed tools, plus a final confirmation
  form shown to the person by the host (MCP elicitation) where the host supports it.

Every JSON output carries `schema_version`. If the version is not one this skill knows, ignore
the engine and use the base flow, telling the person why.

## What changes in the flow

- Collect, split, order, and per-block context come from the engine instead of `git grep`.
- State lives where the engine keeps it; do not also write `state.json`.
- The banner says **engine** mode.
- Explanations still come from you, under the same guardrails.
