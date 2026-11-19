# Review state: saving, resuming, invalidating

The state lets a review pause and resume. It records decisions and a fingerprint of the code,
never the code itself.

## Where

`<git-dir>/reviewcheck/state.json`, where `<git-dir>` is the output of `git rev-parse --git-dir`.

Inside the git directory the file is never committed, belongs to one repository (and one
worktree), and needs no `.gitignore` entry. Create the folder if it does not exist.

## Shape

Follow [../assets/state.schema.json](../assets/state.schema.json). In short:
- `schema_version`, `source` (what is being reviewed), `head` (`git rev-parse HEAD`),
  `context_level`, `mode`;
- `files`: for every file in the plan, its content fingerprint from `git hash-object <path>`;
- `blocks`: id, title, citations, `status` (`pending`, `accepted`, `correction_requested`,
  `reopened`), and the person's `note` for corrections;
- `current_block_id` and `closed`.

## When to write

- After planning: all blocks `pending`, `current_block_id` = first block.
- After every decision: update that block, move `current_block_id`, write the file immediately.
- On "pause": write and confirm where the review will resume.
- On close: set `closed: true` after the person confirms the summary.

## Resuming

1. Read the state. If `closed` is true, offer a new review instead.
2. Recompute `git hash-object` for every file in `files`.
3. For each file whose fingerprint changed: set its accepted or corrected blocks to `reopened`,
   tell the person which blocks and why ("this file changed since you reviewed it").
4. If new changed files appeared, offer to add them as new blocks.
5. Tell the person where they are: block k of N, decisions so far, blocks reopened.

## Never

- Never store code, explanations, or file contents in the state.
- Never mark a block decided without the person's explicit words.
