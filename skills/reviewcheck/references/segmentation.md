# Collecting the changes and splitting them into blocks

Goal: turn a set of changes into small, ordered blocks a person can understand one at a time.
A good split lowers the load of each step without hiding how the pieces interact.

## 1. Collect

Pick the source from what the person asked; default to the working changes.

| Source | Diff | New files |
|---|---|---|
| Working changes (default) | `git diff HEAD --no-color -U3` | `git ls-files --others --exclude-standard` (read each as fully added) |
| Staged | `git diff --cached --no-color -U3` | included in the diff |
| Range `A..B` / `A...B` | `git diff --no-color -U3 A...B` | included in the diff |
| Single commit `C` | `git show --no-color -U3 C` | included in the diff |
| Pull request | see [pull-request.md](pull-request.md) | included in the diff |

Rules:
- Never include ReviewCheck's own state (`.git/reviewcheck/`).
- Use `git rev-parse --verify -q HEAD` to verify if HEAD exists. If not, use `git diff --cached` instead of `git diff HEAD`.
- Skip binary, generated, minified, and lock files (e.g. `*.min.js`, `package-lock.json`,
  `*.lock`, `bin/`, `obj/`, `dist/`): list their paths in the plan, do not make blocks of them.
- Secret files: path only, never content (SKILL.md guardrail 9).
- Large changes: above roughly **800 changed lines**, tell the person and propose splitting the
  review by area (folder / feature) or across sessions before planning everything.

## 2. Split into blocks

- **One block = one unit of intent**: a changed function, method, or member; or a group of hunks
  in the same file that only make sense together.
- **Trivial changes go in one "minor changes" block**: imports, formatting, renames with no
  behavior change, comment-only edits. Say what kind they are.
- **Non-code files** (config, docs, CI) are grouped by kind, one block per kind unless a change
  is substantial on its own.
- **Size budget**: if a block's code exceeds ~60 lines, show the changed lines with one line of
  context around each hunk and `…` between gaps, not the whole member.
- **Tests** are their own blocks, placed right after the code they exercise.

## 3. Deleted Code

- Deleted code is a block on its own, never attached to the next block; exception: trivial deletions (no behavior change) go in the "minor changes" block;
- code is taken from the `-` lines of the diff, with line numbers from the old file, cited as [removed] path:start-end;
- search who still uses the symbol (see codebase-context.md): remaining callers go in the Context field with [context], and are a seam; they do not become blocks;
- say what replaces it, if anything replaces it, linking to the new code's block by title instead of re-explaining it.
- 
## 4. Order

1. Definitions before the code that uses them (types, constants, helpers first).
2. Then the code that wires them together (configuration, registration, call sites).
3. Tests right after what they test.
4. Ties: by file path, then by line number. The order must be the same if you plan again.
5. **Cycles.** When blocks depend on each other (A uses B and B uses A, directly or through
   other blocks), start with the block that code outside the cycle calls. If several blocks
   in the cycle are called from outside, or none is, break the tie with rule 4. Present the
   blocks of the cycle one after another, link them by title in Links, and tell the person
   they depend on each other. When more than one is called from outside, say so; when none
   is, say the cycle is not used yet by the rest of the code.

## 5. Seams (at most 3)

A seam is a place where two pieces must agree and a misunderstanding would hide. Only flag
a seam when there is a concrete signal:
- a signature or contract changed and callers exist outside the changes
  (see [codebase-context.md](codebase-context.md));
- a test covering the changed behavior exists and was not touched;
- two blocks change the two sides of the same interaction (producer and consumer).

When there are more than 3 seams, rank them and show the first 3 in the plan:
1. By signal type: changed contracts first, then both sides of the same interaction, then
   tests not touched.
2. Same type: silent changes before changes the compiler catches. A change is **silent** when
   the signature (name, parameters, types) stays the same but what the code returns or does
   changes, or when the language has no compile-time type checking.
3. Still tied: the one with the most callers outside the changes first.
4. Still tied: rule 4 of the Order section (file path, then line number).

Seams left out of the plan are not dropped: show each one in the `Context:` field of the block
it concerns (test seams in the `Tests:` field) when the person reaches that block.

Describe each seam as a question to keep in mind, never as a problem found.

## 6. The plan you present

- Title (one line, from what the changes do).
- Number of blocks and files; files skipped and why.
- Estimate: about 1 minute per 25 changed lines, rounded up per block.
- Up to 3 seams.
- "Shall we start with the first block?"


