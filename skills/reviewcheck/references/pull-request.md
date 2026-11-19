# Reviewing a pull request

Requires the GitHub CLI (`gh`) authenticated by the person. Without it, review the PR locally
(checked-out branch) and close with a local outcome only.

## Choosing the PR

- If the person names a PR, use it.
- If they ask what is open: `gh pr list --json number,title,author`. Leave out PRs whose author
  login equals `gh api user --jq .login`: you cannot approve your own work.

## Reading it

- Prefer a local checkout (`gh pr checkout <n>`) for **full** context; ask before switching
  branches if the working tree has uncommitted changes.
- Otherwise read the diff with `gh pr diff <n>` and fetch only the files you need: context level
  **partial**.

## Self-review

At the start, compare the PR author with `gh api user --jq .login`. If they match, tell the
person **before the first block**, in one line: this is their own PR, so it can only close as a
comment, never as an approval or a request for changes. Then review normally.

## Closing

1. With every block decided, compute the outcome from the state, not from memory:
   - own PR → `comment`;
   - any correction requested → `request changes`;
   - all accepted → `approve`.
2. Show a **preview**: the outcome and the exact body that would be posted. Nothing is posted yet.
3. Post only after the person explicitly confirms (e.g. "confirm" / "conferma"). A reply that is
   not a clear confirmation means "do not post".
4. Post one review:
   `gh pr review <n> --approve | --request-changes | --comment --body "<body>"`.
5. The body lists the person's correction notes with their citations (`path:start-end`), and an
   attestation line: number of blocks reviewed, head commit, number of corrections, and that the
   decisions were confirmed by the person.

## Never

- Never post without the explicit confirmation in step 3.
- Never approve or request changes on the person's own PR.
- Never put your own explanations in the posted body: only the person's notes.
