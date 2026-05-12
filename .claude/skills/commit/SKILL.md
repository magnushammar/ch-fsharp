---
name: commit
description: Use when the user asks to commit, check in, save, or ship changes. Analyses current diff, groups changes into logical commits, and commits them. Never amends, force-pushes, or skips hooks. Never commits secrets. Does NOT sync docs / CLAUDE.md / auto-memory — those are separate, manually-triggered workflows.
allowed-tools: Bash(git *), Read
argument-hint: [optional commit guidance]
---

# Commit workflow

Just commit. Do not sync docs, CLAUDE.md, or auto-memory here — the
user will ask separately for those when they want them.

## Phase 1 — gather state

Run in parallel:

- `git status` (never use `-uall`)
- `git diff` (unstaged changes)
- `git diff --cached` (already staged changes)
- `git log --oneline -5` (recent commit style reference)

## Phase 2 — plan and commit

Group changes into logically cohesive commits. Each commit = one
concern (feature / fix / refactor / doc update). Split unrelated
concerns even if they share a file.

- **Single obvious group** (one clear concern) → commit straight
  through, no confirmation ceremony.
- **Multiple groups** → briefly list them (one line each) and
  proceed. The user can interrupt if they disagree with the
  grouping; default is to keep moving.

If the user passed `$ARGUMENTS`, treat them as grouping or message
guidance.

For each group, in order:

1. **Stage** with specific paths: `git add <file> ...`. Never
   `git add -A` / `git add .` — secrets leak that way.
2. **Commit** with a HEREDOC message focused on *why*. Match the
   style of Phase 1's recent commits.

   ```
   git commit -m "$(cat <<'EOF'
   Your message here

   Co-Authored-By: Claude <noreply@anthropic.com>
   EOF
   )"
   ```
3. **Verify** with `git status`. On pre-commit hook failure, fix the
   underlying issue, re-stage, and create a NEW commit (never
   `--amend`).

## Phase 3 — summary

Once all groups are committed, run `git log --oneline -<N>` (N = new
commits) and show the list. Done.

## Standing rules

- **NEVER amend previous commits** unless the user explicitly asks.
- **NEVER push** unless the user explicitly asks.
- **NEVER use `--no-verify`** or skip pre-commit hooks.
- **NEVER commit secrets** (.env, credentials, keys). Inspect file
  names in Phase 1 and bail with a warning if anything suspicious
  shows up in the staged set.
- **NEVER combine unrelated concerns** into one commit just because
  they were edited in the same session.
- If there are no changes to commit, say so and stop.

## Explicitly NOT in scope

This skill does not:

- Sync or update root `CLAUDE.md` or nested `CLAUDE.md` files.
- Read or update auto-memory (`.claude-memory/`, `MEMORY.md`).
- Invoke `doc-audit`, `fix-memories`, or any other review subagent.

Those are separate, manually-triggered workflows. If the user wants
a documentation audit, they will ask: "audit the docs", "sync
CLAUDE.md", "weed memory". Wait for the explicit ask — do not run
these as follow-ups to commits.
