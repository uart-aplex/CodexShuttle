# Restore Safety and Development Handoff

Updated: 2026-11-07. Development baseline: `0.3.0-preview.13`.

For the consolidated incident/change record and post-restore source recovery procedure, read [the 2026-11-07 recovery handoff](2026-11-07-recovery-handoff.md) first.

## Why This Change Exists

The reported failure scenario is importing an office backup onto a home computer with its own conversations. The previous restore implementation replaces selected Codex state and uses mirror semantics for selected directories. Destination-only conversations/files can disappear; matching files and indexes can be replaced with older office versions.

This is a restore-mode design problem, not evidence that switching model providers inherently deletes conversations. A differential copy still can overwrite and delete: it only avoids copying unchanged files. `HistoryAndTools` preserves authentication/provider configuration, not independent local history.

The safety invariant for the new merge implementation is: importing office data must not remove or overwrite unrelated home conversations or workspace files.

## Choose the Operation Deliberately

| Operation | Intended use | Effect on destination |
| --- | --- | --- |
| Backup | Export a reusable package | Updates the chosen backup package; do not reuse the only office package to preserve home data |
| Merge Restore (preview) | Import selected conversations and missing project files | Adds missing IDs and verified paginated continuations; adds missing workspace files by default; keeps existing project files and local-ahead chats; retains divergent revisions separately; does not merge active-profile settings/tools |
| Disaster Recovery / HistoryAndTools | Replace selected local state from a backup | Can overwrite history and delete destination-only files despite retaining credentials/provider configuration |
| Disaster Recovery / FullProfile | Broader replacement from a backup | Can overwrite profile settings, history and selected workspaces; credentials remain excluded |

Merge supports verified primary `state_5.sqlite` / self-contained legacy JSONL layouts and the reviewed `thread_history_1.sqlite` paginated layout, including compressed rollouts. Existing reconciled nested indexes are left untouched. Unreconciled nested indexes, mixed source history modes and unknown schemas remain blocked. A blocked preview is not permission to delete indexes or use disaster recovery as a substitute. The Desktop GUI acceptance gate remains open.

## Before the Next Import

1. Keep the newest program and read its version. An executable in an older USB package may still perform the old restore workflow.
2. Preserve this computer's current Codex data in a separate, verified local backup while Codex is closed. Use a different backup destination from the incoming office package. Do not overwrite the only copy of either computer's data.
3. Commit and push eligible source changes. Keep private/non-GitHub files and uncommitted work separately protected. Never upload a Codex profile, credentials or raw conversations to this public repository.
4. Close Codex, use Merge Restore, select the package and review Dry Run. **Select New + Updates** selects available Add and Update entries; review both counts before Import Selected. Update verifies that the source contains the complete local history plus a continuation. LocalAhead is skipped. Selected conflicts are retained outside Codex, not added as chats. Checkbox changes do not require another scan; path/data changes do. Confirm the destination profile belongs to this computer. If another process holds the SQLite files open, close it before retrying; do not bypass the private-copy checks.
5. Leave workspace paths unchanged where possible (especially shared E: locations). Preview.11 adds missing files from all enabled backup workspaces when Add missing workspace files is checked. Review its per-workspace summary separately from conversation selection. Existing same-path files remain local even when different. Missing project data absent from the backup still blocks a new conversation. Whole-workspace mappings affect destination files and structured working directories, not arbitrary links in historical text; subfolder mappings require a separate backup root or conversation-only mode.
6. If unsupported layout, corruption or missing paths are reported, stop and retain both originals. Do not use Disaster Recovery just to make the import proceed.
7. Close all Codex Desktop/CLI/IDE sessions before applying. A verified local metadata/index restore point is mandatory. Conflicts remain separate retained files, not automatically forked Desktop chats.

## What Survives an Accidental Overwrite

- GitHub retains pushed commits unless the remote itself is changed. A local mirror restore does not automatically remove those remote commits.
- The local checkout, including its `.git` directory, can still be replaced by Disaster Recovery if the enclosing workspace is selected. A prior push does not make the local directory immune.
- Codex conversations, local-only files, uncommitted changes, ignored build outputs and local restore points are not protected by a source-code push.
- This document preserves the engineering context needed to resume development; it is not a backup of the original conversation.

After an accidental overwrite, first preserve the affected local directory without resetting or deleting it. Obtain a fresh clone of `https://github.com/uart-aplex/CodexShuttle.git` in a separate location outside any selected restore targets and inspect the remote history. Recover source from verified remote commits; rebuild the portable executable if needed. Do not run `git reset --hard` or blindly overwrite remaining local work. Lost chats require a separate Codex backup, not GitHub source recovery.

## Development Handoff

- [Preview.13 browser-cache restore](v0.3.0-preview.13-browser-cache-restore.md): retain an active recognized Chrome plugin cache instead of blocking the entire restore. Nested exclusions are honored by copying, Dry Run and path remapping; missing source ancestors are also protected against recursive mirror deletion. Log and final result disclose the exception. Other running executables remain guarded. No production restore was performed.
- [Preview.12 incremental conversations](v0.3.0-preview.12-incremental-conversations.md): ordered rollout and projection prefix checks classify Update/LocalAhead; divergent branches remain external conflicts. Updates preserve original rollout files and local thread metadata, activate a new immutable revision, and replace only the selected thread's projection. The journal now restores updated rows as well as removing additions, including partial attached-database commits. Standard suite: 151 core + 10 WPF passed; isolated native add/update read/resume tests passed. No production merge was performed.
- [Preview.11 additive workspaces](v0.3.0-preview.11-additive-workspaces.md): transfer missing workspace files before importing chats; never mirror, overwrite, or remove project files. Verified additions remain on partial failure or conversation rollback. New previews fingerprint missing-file contents; Apply can reuse approved hashes for unchanged source metadata but verifies every copied temporary file against the approved hash before a no-overwrite rename. Metadata is checked again after workspace transfer. Tests include missing-project native read/resume, cancellation/retry, path guards and collision/content changes.
- Preview.10 clarifies actual import counts and adds Select New. A completed conflict-retention operation is not evidence of newly visible conversations. New threads can be blocked by a missing working directory; the recorded working directory can differ from the project name the user remembers. Check manifest metadata and the final operation journal before concluding a chat was imported. Do not silently map a missing project to a similarly named directory, create an empty placeholder to bypass checks, or use mirror restore to copy unrelated workspace data. Standard validation: 123 core and 9 WPF tests passed, four opt-in tests skipped.
- [Preview.9 conflict classification](v0.3.0-preview.9-conflict-classification.md): compare paginated content separately from storage/migration metadata. Equivalent entries are no-ops, not overwrites. Exact checksums, approval fingerprints and rollback checks remain unchanged.
- [Preview.8 paginated merge](v0.3.0-preview.8-paginated-merge.md): new-ID imports preserve physical rollout bytes and SQLite projection offsets together; both metadata databases participate in local rollback. Same-ID divergence remains an external retained conflict, not automatic message merging. Selection and terminal-status regressions are covered by WPF view-model tests.
- [Preview.5 partial-restore fix](v0.3.0-preview.5-partial-restore-fix.md): copy errors previously bypassed automatic path repair. Running target executables now block before copying; failed copies only trigger index-path recovery after matching history file sets and hashes. A recovered index does not make the restore successful. Localized copy diagnostics are retained. 109 tests passed, with two opt-in app-server tests skipped on this machine.
- Main repository: `uart-aplex/CodexShuttle`; Windows WPF / .NET 8.
- [Technical design](v0.3-safe-merge-design.md): researched official app-server APIs and the supplied `tang12306/codex-sync` reference. Reference code was not copied or executed.
- [Original validation report](v0.3-test-results.md): 73 passing cases for preview.1. [Preview.3 fix report](v0.3.0-preview.3-restore-fix.md): updated core and WPF regression results, including isolated native compressed-rollout read/resume; Desktop GUI acceptance pending.
- [Preview.4 checksum fix](v0.3.0-preview.4-checksum-fix.md): 97 passing tests; database inspection no longer opens original package databases through SQLite. Do not use earlier builds for package inspection.
- `src/CodexShuttle.Core/Merge/`: separate merge inventory, validation, SQLite adapter, verified local snapshots and journal recovery. Never route merge through the mirror restore service.
- `src/CodexShuttle.App/ViewModels/MergeViewModel.cs`: explicit selection, preview invalidation, manual confirmation and rollback UI. No automatic synchronization.
- Existing `RestoreService`, `RestorePlanner` and mirror-copy code remain disaster-recovery operations. Do not describe them as safe merge.
- Next gate: isolated Desktop GUI acceptance and reviewed adapters for nested/external indexes. Never experiment on production profiles without explicit approval.
- Later phases remain unimplemented: stable project/device registry, persistent Local Only policies, workspace sync, immutable cloud transport, encryption, ancestry-aware forks and ownership leases.

Do not infer completion from an old backup folder's date or a restored executable's presence. Verify the GitHub commit, actual program version, mode and final operation result.

## Recover the Fixes After Workspace Replacement

Before replacement restore, keep the new executable and independent recovery data outside every selected restore target, for example under `%USERPROFILE%\CodexShuttle-Safety`. Do not restore that folder from the incoming package. A project-specific recovery copy is not necessarily a standard Shuttle package or a one-click merge source.

Remote GitHub commits survive a local workspace replacement. Once restore is finished, inspect the restored checkout:

```powershell
Set-Location 'E:\CodexWorkspace\SoftwareDevelopment\CodexShuttle'
git status --short
git remote -v
```

Only if there are no local changes, `origin` is `https://github.com/uart-aplex/CodexShuttle.git`, and this is the intended `main` checkout, update without discarding local commits:

```powershell
git switch main
git fetch origin
git merge --ff-only origin/main
git log -1 --oneline
```

If the checkout is dirty, diverged, missing `.git`, or has a different remote, stop. Preserve it and clone the correct repository to a new empty directory outside restore targets instead. Do not use `reset --hard`, force-push or delete remaining local changes to make recovery succeed.

**Updating source does not update an ignored executable in `outputs/` or a USB package.** Run the separately protected `0.3.0-preview.13` executable, or rebuild from the recovered source with .NET SDK 8:

```powershell
dotnet publish src/CodexShuttle.App/CodexShuttle.App.csproj -p:PublishProfile=Portable-win-x64 -p:PublishDir="$env:USERPROFILE\CodexShuttle-Safety\rebuilt-preview13\"
```

Check the actual program version before use. GitHub source recovery cannot restore local-only chats; those require their independent recovery data. Replacement restore still overwrites selected state even when these compatibility fixes are installed.
