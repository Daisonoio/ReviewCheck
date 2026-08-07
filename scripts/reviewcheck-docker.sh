#!/usr/bin/env bash
# Wrapper that lets the "reviewcheck" Docker image be registered ONCE, globally, and still review
# whichever repo Claude Code currently has open — the same "register once" property the native
# binary has.
#
# Why this exists: `docker run -v "$PWD:/repo" ...` registered directly with
# `claude mcp add --scope user` bakes $PWD in at REGISTRATION time (the host stores literal argv,
# it doesn't re-run a shell per launch) — so it would silently keep reviewing whatever folder was
# current when you ran `claude mcp add`, not the repo you open later. This script is the fix: Claude
# Code launches it fresh for every session with its CWD set to the open project (the same mechanism
# the native binary relies on), so "$PWD" below is resolved at every launch, not once.
#
# Prerequisite: `docker build -t reviewcheck .` (from the ReviewCheck repo root) at least once, and
# again whenever src/ changes.
#
# Register this script itself — not the raw `docker run` command — with Claude Code:
#   claude mcp add reviewcheck --scope user /absolute/path/to/ReviewCheck/scripts/reviewcheck-docker.sh
set -euo pipefail

exec docker run -i --rm \
  -v "$PWD:/repo" \
  -e REVIEWCHECK_ANTHROPIC_KEY \
  -e ANTHROPIC_API_KEY \
  -e REVIEWCHECK_LLM_MODEL \
  -e REVIEWCHECK_NARRATOR \
  -e GITHUB_TOKEN \
  reviewcheck
