@echo off
REM Wrapper that lets the "reviewcheck" Docker image be registered ONCE, globally, and still review
REM whichever repo Claude Code currently has open.
REM
REM Use cmd, not PowerShell, on Windows: when Claude Code launches a subprocess with redirected
REM (piped) stdio rather than a real console -- exactly how it starts an MCP server -- PowerShell's
REM pipeline can re-interpret the child process's raw output (encoding/buffering) instead of passing
REM it through untouched, which can corrupt the MCP JSON-RPC stream on stdout. cmd.exe does not do
REM this -- it launches the child process with inherited handles and does not reinterpret its output.
REM Confirmed on real hardware: PowerShell hung with a silent connection timeout; cmd connects fine.
REM
REM Prerequisite: `docker build -t reviewcheck .` (from the ReviewCheck repo root) at least once, and
REM again whenever src/ changes.
REM
REM Register this script itself -- not `docker run` directly -- with Claude Code:
REM   claude mcp add reviewcheck --scope user cmd -- /c "C:\path\to\ReviewCheck\scripts\reviewcheck-docker.cmd"
docker run -i --rm -v "%CD%:/repo" -e REVIEWCHECK_ANTHROPIC_KEY -e ANTHROPIC_API_KEY -e REVIEWCHECK_LLM_MODEL -e REVIEWCHECK_NARRATOR -e GITHUB_TOKEN reviewcheck
