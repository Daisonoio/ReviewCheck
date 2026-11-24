# Presenting a block

Every block uses the same shape, so the person always knows where to look.

## Format

```
Block <k> of <N> — <title>

<code, with real line numbers from the file, exactly as it is on disk>
[diff] <path>:<start>-<end>

What:   <what this code concretely does — name the real identifiers>
Why:    <why it exists in this change / what it connects to>
Links:  <related blocks by title; context facts like callers or tests, each with a citation>
Context: <optional, at most 2 excerpts of ≤15 lines, each with [context] path:start-end>
Uncertainty: <what you could not verify, or "none">
Tests:   <tests that cover this block, if any; if none, don't show>
Accept this block, or request a correction (with a note)?
```

## Rules

- **Code comes from the file**, read at presentation time, with its real line numbers. Never
  retype or paraphrase code.Deleted code is the exception: see below.
- **Be specific.** Name the methods, variables, types, and constants involved. Describe the
  concrete operation, not a category: avoid "handles", "manages", "processes" unless you say
  exactly what is handled and how.
- **Refer to blocks by title and position**, not by internal ids.
- **No verdict words** in What / Why / Links: correct, incorrect, safe, unsafe, secure, insecure,
  approved, bug, buggy, broken, flawed, wrong, vulnerable, "looks good", "works as intended".
  Doubt belongs in Uncertainty, phrased as a question.
- **Uncertainty is mandatory when** the explanation depends on code outside what you read, on a
  name search that may have matched the wrong symbol, or on runtime behavior you cannot see.
- **Stop after the question.** Do not present the next block in the same turn.
- **Deleted code** is cited as `[removed] path:start-end`. Take the code from the `-` lines of
  the diff; the line numbers refer to the file before the change, not the current one.
## Recovery

If the person says "show the code", "with citations", or "uncertainty?", present the current
block again, whole, in this format. Do not apologize; just recover.
