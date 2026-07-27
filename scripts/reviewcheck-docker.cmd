@echo off
REM Wrapper that lets the "reviewcheck" Docker image be registered ONCE, globally, and still review
REM whichever repo Claude Code currently has open.
REM
REM Prefer this over reviewcheck-docker.ps1 on Windows: when Claude Code launches a subprocess with
REM redirected (piped) stdio rather than a real console, PowerShell's pipeline can re-interpret the
REM child process's raw output (encoding/buffering) instead of passing it through untouched, which
REM can corrupt the MCP JSON-RPC stream on stdout. cmd.exe does not do this -- it launches the child
REM process with inherited handles and does not reinterpret its output.
REM
REM Prerequisite: `docker build -t reviewcheck .` (from the ReviewCheck repo root) at least once, and
REM again whenever src/ changes.
REM
REM Register this script itself -- not `docker run` directly -- with Claude Code:
REM   claude mcp add reviewcheck --scope user cmd -- /c "C:\path\to\ReviewCheck\scripts\reviewcheck-docker.cmd"
docker run -i --rm -v "%CD%:/repo" -e REVIEWCHECK_ANTHROPIC_KEY -e ANTHROPIC_API_KEY -e REVIEWCHECK_LLM_MODEL -e REVIEWCHECK_NARRATOR reviewcheck
