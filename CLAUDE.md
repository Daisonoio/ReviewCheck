# CLAUDE.md

Project memory for Claude Code. The canonical contributor guide is **[`AGENTS.md`](AGENTS.md)** —
read it for the build/test/eval commands, the repo map, the guarantees you must not break, and the
conventions. This file only adds the Claude-Code-specific notes.

## Single source of truth

Don't duplicate `AGENTS.md` here. If guidance for agents changes, edit `AGENTS.md`; this file stays a
thin pointer so the two can never drift.

## Claude-Code-specific notes

- **Two agent artifacts, two audiences.** `AGENTS.md` is for an agent *developing* this repo.
  `agent/reviewcheck.agent.md` (mirrored in `.claude/agents/`) is the *product* agent that runs
  ReviewCheck for an end user. Don't edit one expecting the other to change.
- **The product also ships as a skill** — `.claude/skills/reviewcheck-guided-review/SKILL.md` is the
  Claude Code "golden path" packaging. It and the product agent share the same substance; keep them in
  sync when the flow changes.
- **The MCP server is `reviewcheck`.** Register it (`claude mcp add reviewcheck --scope user …`) before
  the guided-review skill/agent can call its tools.
- **Warnings are errors.** Any change must build clean under `TreatWarningsAsErrors`.
