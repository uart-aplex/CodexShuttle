# Restore Safety and Development Handoff

Updated: 2026-10-10. Development baseline: `0.3.0-preview.1`.

## Why This Change Exists

The reported failure scenario is importing an office backup onto a home computer with its own conversations. The previous restore implementation replaces selected Codex state and uses mirror semantics for selected directories. Destination-only conversations/files can disappear; matching files and indexes can be replaced with older office versions.

This is a restore-mode design problem, not evidence that switching model providers inherently deletes conversations. A differential copy still can overwrite and delete: it only avoids copying unchanged files. `HistoryAndTools` preserves authentication/provider configuration, not independent local history.

The safety invariant for the new merge implementation is: importing office data must not remove or overwrite unrelated home conversations or workspace files.

## Choose the Operation Deliberately

| Operation | Intended use | Effect on destination |
| --- | --- | --- |
| Backup | Export a reusable package | Updates the chosen backup package; do not reuse the only office package to preserve home data |
| Merge Restore (preview) | Explicitly import selected conversations | Adds missing IDs; skips identical content; retains divergent revisions separately; never copies workspaces/settings/tools |
| Disaster Recovery / HistoryAndTools | Replace selected local state from a backup | Can overwrite history and delete destination-only files despite retaining credentials/provider configuration |
| Disaster Recovery / FullProfile | Broader replacement from a backup | Can overwrite profile settings, history and selected workspaces; credentials remain excluded |

Safe merge support is restricted to verified primary `state_5.sqlite` / legacy JSONL layouts. Nested or unknown database layouts are blocked. A blocked preview is not permission to delete indexes or use disaster recovery as a substitute. The Desktop GUI acceptance gate remains open.

## Before the Next Import

1. Keep the newest program and read its version. An executable in an older USB package may still perform the old restore workflow.
2. Preserve this computer's current Codex data in a separate, verified local backup while Codex is closed. Use a different backup destination from the incoming office package. Do not overwrite the only copy of either computer's data.
3. Commit and push eligible source changes. Keep private/non-GitHub files and uncommitted work separately protected. Never upload a Codex profile, credentials or raw conversations to this public repository.
4. Use Merge Restore, select the package, review Dry Run and explicitly select conversations. Selection/path changes require another Dry Run. Confirm the destination profile belongs to this computer.
5. Leave workspace paths unchanged where possible (especially shared E: locations). Merge Restore itself does not copy workspace files. Different-drive mappings affect imported structured working directories, not arbitrary links in historical message text.
6. If unsupported layout, corruption or missing paths are reported, stop and retain both originals. Do not use Disaster Recovery just to make the import proceed.
7. Close all Codex Desktop/CLI/IDE sessions before applying. A verified local metadata/index restore point is mandatory. Conflicts remain separate retained files, not automatically forked Desktop chats.

## What Survives an Accidental Overwrite

- GitHub retains pushed commits unless the remote itself is changed. A local mirror restore does not automatically remove those remote commits.
- The local checkout, including its `.git` directory, can still be replaced by Disaster Recovery if the enclosing workspace is selected. A prior push does not make the local directory immune.
- Codex conversations, local-only files, uncommitted changes, ignored build outputs and local restore points are not protected by a source-code push.
- This document preserves the engineering context needed to resume development; it is not a backup of the original conversation.

After an accidental overwrite, first preserve the affected local directory without resetting or deleting it. Obtain a fresh clone of `https://github.com/uart-aplex/CodexShuttle.git` in a separate location outside any selected restore targets and inspect the remote history. Recover source from verified remote commits; rebuild the portable executable if needed. Do not run `git reset --hard` or blindly overwrite remaining local work. Lost chats require a separate Codex backup, not GitHub source recovery.

## Development Handoff

- Main repository: `uart-aplex/CodexShuttle`; Windows WPF / .NET 8.
- [Technical design](v0.3-safe-merge-design.md): researched official app-server APIs and the supplied `tang12306/codex-sync` reference. Reference code was not copied or executed.
- [Validation report](v0.3-test-results.md): 73 passing cases, including isolated installed app-server list/read/resume; Desktop GUI acceptance pending.
- `src/CodexShuttle.Core/Merge/`: separate merge inventory, validation, SQLite adapter, verified local snapshots and journal recovery. Never route merge through the mirror restore service.
- `src/CodexShuttle.App/ViewModels/MergeViewModel.cs`: explicit selection, preview invalidation, manual confirmation and rollback UI. No automatic synchronization.
- Existing `RestoreService`, `RestorePlanner` and mirror-copy code remain disaster-recovery operations. Do not describe them as safe merge.
- Next gate: isolated Desktop GUI acceptance and reviewed adapters for nested/external indexes. Never experiment on production profiles without explicit approval.
- Later phases remain unimplemented: stable project/device registry, persistent Local Only policies, workspace sync, immutable cloud transport, encryption, ancestry-aware forks and ownership leases.

Do not infer completion from an old backup folder's date or a restored executable's presence. Verify the GitHub commit, actual program version, mode and final operation result.
