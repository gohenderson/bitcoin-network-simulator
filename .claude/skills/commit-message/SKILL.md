---
name: commit-message
description: Use when the user asks to draft, write, or generate a commit message for pending changes without actually committing — e.g. "draft a commit message", "write a commit message file", "create COMMIT_MESSAGE.txt", "summarize these changes for a commit", or "what would a good commit message be for this". Examines the working tree's pending (staged, unstaged, and untracked) changes and writes a drafted message to COMMIT_MESSAGE.txt at the repo root. Does not stage, commit, or push anything.
---

# Commit message drafting

Produces a `COMMIT_MESSAGE.txt` file at the repo root describing the
repo's current pending changes — for the user to review, edit, and use
themselves (e.g. `git commit -F COMMIT_MESSAGE.txt`). This skill never
runs `git add`, `git commit`, or `git push`.

## Steps

1. Confirm you're in a git repository (`git rev-parse --is-inside-work-tree`). If not, tell the user and stop.

2. Gather the pending changes in parallel:
   - `git status --short` (never `-uall`) — staged, unstaged, and untracked files.
   - `git diff HEAD` — the full diff of everything not yet committed (staged + unstaged together). If that's empty but `git status` shows untracked files, also read the untracked files that look relevant (skip anything that looks like a build artifact, dependency lock churn, or generated output).
   - `git log --oneline -10` — recent commit message style/conventions to match (tense, prefix conventions, length).

3. If there are no pending changes at all (clean working tree, nothing staged, nothing untracked), tell the user there's nothing to describe and stop — do not write a placeholder file.

4. Analyze the actual diff content, not just filenames. Identify the nature of the change (new feature, enhancement, bug fix, refactor, docs, test) and, more importantly, the **why** behind it — infer motivation from the code itself (what problem it solves, what behavior it changes) and from conversation context if this skill was invoked mid-task. Don't just restate filenames or "modified X.cs".

5. Draft the message:
   - First line: concise summary (under ~70 characters where reasonable), in the imperative mood, matching this repo's existing log style from step 2.
   - Blank line, then 1-3 sentences of body focused on *why*, not a mechanical list of every changed line — the diff itself already shows what changed.
   - Do not invent a scope/ticket/issue reference that isn't evidenced by the code or conversation.
   - Do not add any messaging indicating co-authorship whatsoever.

6. Write the drafted message to `COMMIT_MESSAGE.txt` in the repo root with the Write tool, overwriting any existing copy.

7. Check whether `COMMIT_MESSAGE.txt` is covered by `.gitignore` (`git check-ignore COMMIT_MESSAGE.txt`). If it is not ignored, mention this to the user once, so a scratch commit-message draft doesn't accidentally get committed — but do not edit `.gitignore` yourself without being asked.

8. Report back concisely: where the file was written, and the drafted subject line. Do not print the full message body again if you already wrote it to the file — the user can read it there.
