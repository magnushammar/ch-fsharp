---
name: weed-memories
description: Scan auto-memory files across all projects on this machine and identify entries that contradict the current project's CLAUDE.md direction, then offer to delete the biasing ones. Use when the auto-memory system is surfacing stale framing that anchors thinking toward archived or retired work.
disable-model-invocation: true
---

# Weed biasing memories

Find memory files — across every project on this machine — that would bias a future session toward a direction the user has since rejected, and offer to delete them. Never delete without explicit user approval.

## Process

1. **Establish ground truth for the current project.**
   - Read the current project's `CLAUDE.md` at the repo root.
   - If `.claude-memory/MEMORY.md` exists in the repo, read it — it is authoritative over the auto-memory.
   - Extract the active direction, scope, and any explicit "off-scope" / "archived" markers.

2. **Enumerate every memory file on this machine.**
   - Use Glob: `/home/hammar/.claude/projects/*/memory/*.md`
   - Include all projects, not only the current one. Cross-project anchoring is the bug we're hunting.

3. **Classify each memory file** by reading it:
   - **keep** — consistent with the active direction; or purely general process / tooling guidance (workflow preferences, planning rules, ClickHouse gotchas, language/perf findings, data-infrastructure facts).
   - **retire-candidate** — contradicts the active direction; describes archived work; frames the task in a way the user has explicitly rejected; references specific tables, files, or artifacts that no longer exist.
   - **verify** — references specific code, paths, or state that cannot be confirmed without reading the repo. Not auto-retired; just flagged.

4. **Produce a report** grouped by classification. For each `retire-candidate`, show:
   - Full path to the memory file
   - One-line reason (e.g. "frames project as momentum-prediction; current direction is neutral feature extraction")
   - First 3–5 lines of the memory body for user context

5. **Ask before deleting.** List the retire candidates and wait for explicit approval (per-file or a batch "delete all listed"). After approval:
   - Delete the file
   - Remove its line from the containing directory's `MEMORY.md` index
   - Do not delete the `MEMORY.md` index itself.

## What counts as biasing

- Describes a task/direction the user has since archived (e.g. spike prediction when current work is neutral feature extraction)
- Names tables, files, or artifacts that were dropped / archived / moved
- Locks in a framing ("two-tier momentum strategy", "predict pump events", "ride the retail tail") that contradicts the current `CLAUDE.md`
- Recommends a specific detector or algorithm whose scaffolding has been archived

## What is NOT biasing

- General ClickHouse / shell / language gotchas
- Process preferences (save queries as script files, planning workflow, thoroughness, no-autostart, detection-scope discipline, no calendar analysis)
- Language or performance findings that are codebase-agnostic
- Data-infrastructure descriptions (data gaps, import patterns) — unless CLAUDE.md has superseded them

## Caveats

- Do **not** extract zip archives, even if they are referenced in memories.
- Do **not** read files inside `archive/` directories of any project.
- Do **not** touch `.claude-memory/` directories inside any repo — those are user-authoritative and out of scope.
- When borderline, classify `verify` rather than `retire-candidate`. Err toward preservation and let the user decide.
- Run this skill `/weed-memories` manually only; do not auto-invoke, side effects are destructive.
