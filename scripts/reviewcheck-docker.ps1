<#
.SYNOPSIS
    Wrapper that lets the "reviewcheck" Docker image be registered ONCE, globally, and still review
    whichever repo Claude Code currently has open.

.DESCRIPTION
    `docker run -v "$PWD:/repo" ...` registered directly with `claude mcp add --scope user` bakes the
    path in at REGISTRATION time (the host stores literal args, it doesn't re-run a shell per launch)
    — so it would silently keep reviewing whatever folder was current when you ran `claude mcp add`,
    not the repo you open later. This script is the fix: Claude Code launches it fresh for every
    session with its working directory set to the open project, so the path below is resolved at
    every launch, not once.

    Prerequisite: `docker build -t reviewcheck .` (from the ReviewCheck repo root) at least once, and
    again whenever src/ changes.

    Register this script itself — not the raw `docker run` command — with Claude Code:
        claude mcp add reviewcheck --scope user pwsh -File "C:\path\to\ReviewCheck\scripts\reviewcheck-docker.ps1"
#>

$ErrorActionPreference = 'Stop'
$repoPath = (Get-Location).Path

docker run -i --rm `
  -v "${repoPath}:/repo" `
  -e REVIEWCHECK_ANTHROPIC_KEY `
  -e ANTHROPIC_API_KEY `
  -e REVIEWCHECK_LLM_MODEL `
  -e REVIEWCHECK_NARRATOR `
  reviewcheck

exit $LASTEXITCODE
