# CLAUDE.md

Project memory for Claude Code. ReviewCheck is being rebuilt as a portable agent skill; the
original .NET MCP server is kept, frozen, under `V 0.1/`.

## Repository layout

- `skills/reviewcheck/` — **the product now**: an Agent Skill (`SKILL.md` + `references/` +
  `assets/`). Plain Markdown read by any skill-capable assistant (Claude Code, GitHub Copilot,
  Cursor, …). Keep it host-neutral: no host-specific tool names in its instructions.
- `V 0.1/` — the v0.1 implementation (C# / .NET 8 MCP server, tests, evals, docs). Its contributor
  guide is [`V 0.1/AGENTS.md`](V%200.1/AGENTS.md); run every .NET command from inside that folder.
  It is planned to become the skill's optional engine (`skills/reviewcheck/references/engine.md`).

## Notes

- **`SKILL.md` stays short.** Flow and guardrails live there; details go in `references/`, which
  the assistant reads only when a step points to them.
- **Guardrails are the point of the product.** Co-presence, grounding, no verdict, one block per
  turn, the person decides, nothing posted without confirmation. A change that weakens one is
  wrong by design.
- **`V 0.1/` builds clean under `TreatWarningsAsErrors`.** CI runs its build, tests, and evals with
  `working-directory: "V 0.1"`.
