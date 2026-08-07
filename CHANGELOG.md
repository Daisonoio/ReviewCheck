# Changelog

Notable changes to ReviewCheck, grouped by milestone. This project has no tagged releases yet — see
[Roadmap](README.md#roadmap) for what "done" means at each phase — so everything below lives under
`[Unreleased]` until the first tag exists. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/); dates are UTC, from the git history.

## [Unreleased]

### Hardening (2026-08)
- Fixed a CLI argument-injection vulnerability: a caller-supplied `ref` (e.g. `get_review_plan`'s
  `source.ref`) reached `git` unsanitized, so a value like `--output=/tmp/x` could make `git` write to
  an attacker-chosen path instead of being treated as a revision. Closed with ref validation,
  `--end-of-options`, and `ProcessStartInfo.ArgumentList` instead of a single command-line string
  (`LocalDiffReader`).
- Hardened `LlmOutputParser` against stray braces in a model's surrounding prose: JSON extraction now
  finds a balanced `{...}` span (respecting string literals) and retries at the next candidate on a
  parse failure, instead of a naive first-`{`/last-`}` scan.
- `LocalDiffReader` now verifies that resolved file paths stay under the repository root before
  reading them (defense in depth; git's own diff/ls-files output shouldn't produce paths that escape
  it, but nothing enforced that here before).
- `BlockGuard` now rejects a citation whose line range isn't a well-formed "N" or "N-M" with N <= M —
  previously any non-empty string passed, so a malformed range (e.g. a backwards "15-12") looked
  structurally grounded without actually being one.
- Added code coverage collection to CI (informational, no threshold gate yet).
- De-duplicated the `"github"` platform literal into `PullRequestPlatforms.GitHub`.

### Documentation (2026-08)
- Moved the Visual Studio setup instructions out of the middle of the numbered "Getting started" flow
  (they used to sit between steps 4 and 5) into their own clearly-marked "Visual Studio (experimental)"
  section with an upfront `[!WARNING]` about the known, unresolved connection issue — instead of that
  caveat being easy to miss at the bottom.
- `AGENTS.md` and `README.md` had drifted on the test count; both now say the same number, sourced from
  an actual `dotnet test` run rather than typed by hand.
- Added this `CHANGELOG.md`.

### GitHub pull request review (T1–T4)
- `IPullRequestPlatform` seam + `GitHubPullRequestPlatform` (BYO token via `GITHUB_TOKEN`).
- `get_review_plan` reviews a remote PR the same way as a local diff (read side).
- `list_pull_requests`: browse a repo's open PRs, excluding ones you opened yourself
  (GUARDRAILS G10 — no self-approval).
- `is_self_review` computed once when a PR review opens (whether picked from the list or opened
  directly by number) and disclosed to the user before the first block.
- `submit_review` posts a real review to the PR (`approve` / `request_changes` / `comment_only`),
  gated by explicit `confirm:true` and never offering `approve`/`request_changes` on a self-authored PR.

### MVP-3 — grounded LLM narration
- `ILlmProvider` seam + `AnthropicByoProvider` (BYO key) + `FakeLlmProvider` for tests.
- Prompt builder, JSON output parser, explanation rubric, retry-once, degrade-to-facts on failure.
- Hosting mode: narrate via the host's own MCP sampling when no key is configured.
- Two-tier disclaimer (🟡 grounded vs 🔴 host-interpreted) shown on every review.

### MVP-2 — deterministic analysis pipeline
- `ReviewCheck.Platform`: unified-diff parser + `LocalDiffReader` (git, local process only).
- `ReviewCheck.Pipeline`: Roslyn-based P1–P8 — dependency graph, block segmentation, reading order,
  citations, interaction seams, graceful degradation.
- `PipelineProvider` replaces the stub behind the `IReviewProvider` seam.

### MVP-1 — foundations
- `ReviewCheck.Core`: immutable domain types + `BlockGuard` (co-presence + grounding invariant).
- MCP server (stdio) with the guided-review tool set; local JSON session store under `.reviewcheck/`.
- Recovery commands (R1–R10) verified against the stub provider.

### Infrastructure
- Capability eval suite (`eval/ReviewCheck.Evals`): scores the guardrails over a corpus, gates CI.
- `review_health`: local, opt-in oversight signals (grounding coverage, evaluative-language hits,
  correction/acceptance ratio) — never surfaced unprompted.
- Containerisation (Dockerfile, devcontainer), Dependabot, PR auto-labeling.
- `AGENTS.md` / `CLAUDE.md` / the Claude Code skill packaging as agent-facing infrastructure.
