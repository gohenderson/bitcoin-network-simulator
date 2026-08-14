---
name: commit-pending-changes
description: Use when the user asks to commit pending changes using COMMIT_MESSAGE.txt, "commit with the drafted message", "use COMMIT_MESSAGE.txt for the commit", or otherwise wants the working tree's pending changes committed with that file as the message source. Final step of the commit-message / sanitize-commit-message / commit-pending-changes pipeline. Stages relevant pending changes and runs a real `git commit -F COMMIT_MESSAGE.txt` — this actually creates a commit, unlike the other two skills.
---

# Commit pending changes from COMMIT_MESSAGE.txt

Stages the repo's pending changes and commits them using `COMMIT_MESSAGE.txt`
(at the repo root) verbatim as the commit message, via `git commit -F`. This
skill does not draft or edit the message — that's the `commit-message` /
`sanitize-commit-message` skills — it only consumes it. It never pushes.

Being asked to run this skill is the explicit user request to commit — do
not ask for additional confirmation before committing, but do follow the
safety checks below.

## Steps

1. Confirm you're in a git repository. If not, tell the user and stop.

2. Confirm `COMMIT_MESSAGE.txt` exists at the repo root and is non-empty. If it's missing, tell the user to draft one first (the `commit-message` skill) and stop. Read its contents so you know what you're about to commit.

3. Gather current state in parallel: `git status --short` (never `-uall`) and `git diff HEAD` (staged + unstaged together).

4. If the working tree is entirely clean (nothing staged, unstaged, or untracked), tell the user there's nothing to commit and stop — do not create an empty commit.

5. Review what `git status` shows and stage the pending changes by explicit path — never `git add -A` or `git add .`. For each modified/untracked file:
   - Stage it normally, **unless** it looks like it could contain secrets (`.env`, `*credentials*`, `*secret*`, private key files, etc.) or is an obvious build artifact/dependency lock churn that doesn't belong in the commit — skip those and flag them to the user instead of silently including or silently dropping them.
   - If genuinely unsure whether something belongs, ask rather than guessing.

6. Double-check the now-staged diff (`git diff --staged`) doesn't contain anything that looks like a credential or secret, even in a file whose name looked innocuous.

7. Commit with the message file directly — don't retype or paraphrase it:
   ```
   git commit -F COMMIT_MESSAGE.txt
   ```
   Do not add `--no-verify`, `--no-gpg-sign`, `-c commit.gpgsign=false`, or `--amend` unless the user explicitly asked for one of those in this request.

8. If the commit fails because a pre-commit hook rejected it, do not bypass the hook. Report the hook's output to the user, fix the underlying issue only if it's obvious and small, re-stage, and create a new commit — never amend a commit that didn't actually happen.

9. On success, run `git status --short` to confirm a clean tree and report the resulting commit hash and subject line back to the user. Then clear the contents of `COMMIT_MESSAGE.txt` so it doesn't contain the message just used.
