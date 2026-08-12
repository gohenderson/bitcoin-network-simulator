---
name: commit-workflow
description: Use when the user asks to run the full commit pipeline / workflow — e.g. "draft, sanitize, and commit", "run the commit workflow", "do the whole commit process". Pure orchestrator: runs commit-message, then sanitize-commit-message, then commit-pending-changes, in that order, and nothing else.
---

# Commit workflow

Runs exactly these three skills, in this order, waiting for each to finish
before starting the next (each one depends on the file the previous step
wrote):

1. `commit-message`
2. `sanitize-commit-message`
3. `commit-pending-changes`

Invoke each via the Skill tool by name, sequentially, not in parallel.

Do nothing else: no additional git commands, no extra analysis, no checks
beyond what those three skills already perform themselves. This skill is
pure orchestration — all actual logic lives in the three skills above.

If any step reports that it stopped early (e.g. `commit-message` found no
pending changes, or `commit-pending-changes` hit a hook failure), stop the
pipeline there and report that back — don't proceed to the next step.
