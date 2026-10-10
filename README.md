# Codex Shuttle

Codex Shuttle is an unofficial Windows backup and migration tool for local Codex Desktop, CLI, and IDE state.

It keeps a reusable differential package for moving conversations, workspaces, skills, plugins, rules, and Codex settings between computers. Backup packages are local artifacts and must never be committed to Git.

## Important: Preserve Local History

**Do not use Disaster Recovery (the old Restore tab) to combine histories from two computers.** Both `HistoryAndTools` and `FullProfile` replace selected history/state and can mirror-delete destination-only files. Preserving credentials is not the same as preserving conversations. Differential copying describes how files are transferred, not a safe history merge.

Use **Merge Restore** only for supported profiles after reviewing its Dry Run. It preserves unrelated local conversations and does not copy workspace files. If the preview blocks an unsupported database layout, stop; do not fall back to Disaster Recovery or remove an index to bypass validation. Desktop GUI acceptance is still pending, so keep a separate verified local backup before trying the preview.

Pushing this repository to GitHub protects committed source and documentation, **not Codex chats, private files or uncommitted work**. Disaster Recovery can also overwrite this local checkout if its parent workspace is selected. Remote commits remain recoverable even if the local checkout is replaced. See [restore safety and development handoff](docs/restore-safety-and-handoff.md).

## Features

### v0.3 preview: Safe Merge Restore

The new **Merge Restore** is separate from **Disaster Recovery** (the former Restore tab). It imports only explicitly selected missing conversations, skips identical contents and retains same-ID conflicts outside the active profile. It never mirror-deletes local conversations or copies workspace files, credentials, provider settings or tools. Verified local restore points and guarded rollback are mandatory for merge changes.

This is a restricted **preview**, not automatic synchronization. Primary `state_5.sqlite` with self-contained legacy JSONL is supported; nested indexes and unsupported formats are blocked. Installed app-server list/read/resume tests pass, but isolated Desktop GUI acceptance remains pending. Phase 2 project policies and Phase 3 cloud/encryption are not implemented.

Read the [technical design](docs/v0.3-safe-merge-design.md) and [test results](docs/v0.3-test-results.md) before using the preview. The following full-profile migration features and modes describe Backup/Disaster Recovery, not Merge Restore.

- Detects `%USERPROFILE%\.codex`, `CODEX_HOME`, or a user-selected Codex Home.
- Migrates sessions, archived sessions, SQLite state, memories, skills, plugins, rules, agents, and personal `.agents` marketplace data.
- Lets each computer remember its own USB path while restoring workspaces to their original absolute paths.
- Supports add/remove workspace folders instead of fixed drive assumptions.
- Updates one `CodexShuttle-Current` package by difference.
- Uses a changed-files-only rollback journal so an interrupted update can recover the previous complete package.
- Runs a complete Dry Run across Codex state and every selected workspace.
- Validates package paths, blocks overlapping paths and dangerous restore roots, and checks destination free space.
- Generates SHA-256 checksums for Codex history and migration tools, then verifies them before restore.
- Runs SQLite `quick_check` on copied Codex state.
- Shows the current file, bounded logs, cancellation controls, and incomplete-package markers.
- Verifies the current run ID, manifest, completion marker and Codex/tool checksums from disk before committing and reporting success. Workspace files still use differential mirror copying, not full content checksums.
- Keeps backup/restore logs in resizable right-hand panes with wrapping and optional auto-scroll; settings scroll independently on small screens.

## Operation Results And Logs

Only the final **Backup completed and verified** result confirms a new backup. Individual folder-copy messages are intermediate steps. A failed or canceled update reports that no new backup was saved and identifies the retained backup's date. A cleanup failure is not reported as success.

Progress cannot replace a final result after an operation ends. Each backup, restore and path-repair run writes a separate timestamped operation log under `%LOCALAPPDATA%\CodexShuttle\logs`; use **Open Logs** to find it. Logs are flushed as progress arrives, independent of the on-screen 300-line limit, and include reported errors, the app version and selected paths. They are not a raw transcript of every copied file. Logs may contain private paths; review them before sharing. Older schema-2 packages remain supported for restore.

The backup completion marker participates in rollback along with the manifest and checksum list. Verification or commit failures must restore all of these together so a new marker cannot label an old package as current.

## Migration Modes

`HistoryAndTools` is the default. It restores conversations, local state, memories, skills, plugins, rules, agents, and workspaces while preserving the destination computer's login and provider configuration.

`FullProfile` also mirrors the sanitized Codex profile, including `config.toml` and plugin enablement/configuration. Use it when the destination should use the same Codex setup as the source computer.

Both modes exclude credentials such as `auth.json`, `.env`, key files, and `.sandbox-secrets`. Connected apps and providers may require sign-in again on the destination computer.

## Package Layout

```text
CodexTransfer/
  CodexShuttle-Current/
    .codex/
    .agents/
    <workspace packages>/
    manifest.json
    backup-report.txt
    checksums.sha256
    backup-complete.marker
```

While a differential update is running, only changed or deleted old files are retained in the sibling `CodexShuttle-Current.rollback` transaction folder. It is removed after a verified commit.

## Build

```powershell
dotnet build CodexShuttle.sln
dotnet test CodexShuttle.sln
```

Create the self-contained Windows x64 portable executable with:

```powershell
dotnet publish src/CodexShuttle.App/CodexShuttle.App.csproj -p:PublishProfile=Portable-win-x64
```

## Portable Release

Non-developers can download the portable `.exe` asset from the
[latest GitHub release](https://github.com/uart-aplex/CodexShuttle/releases/latest). It is a self-contained single-file app and does not require a separate .NET installation or DLL download.

Keep the executable on a local drive or USB drive, close Codex, and then run it. Windows SmartScreen may show an unknown-publisher warning because the project does not currently use a paid code-signing certificate. Verify the downloaded file against `SHA256SUMS.txt` from the same release before running it.

The app stores computer-specific paths at:

```text
%LOCALAPPDATA%\CodexShuttle\settings.json
```

## Different Windows User Names

Windows user-profile paths are mapped to the account running the restore. For example, a backup from `C:\Users\OfficeUser\.codex` restores to the current computer's `CODEX_HOME` or `C:\Users\HomeUser\.codex`. Personal `.agents`, roaming AppData, and local AppData targets are mapped the same way. The source computer's user directory is not created on the destination.

After copying, Codex Shuttle also remaps embedded source-profile paths in restored UTF text files and SQLite text fields, including `threads.rollout_path`, JSON-escaped paths, and forward-slash paths. Credentials remain excluded. Only the recorded Codex Home, `.agents`, AppData, and Windows user-profile roots are remapped; fixed workspace paths such as `E:\CodexWorkspace` are deliberately left unchanged so project references remain valid.

Restore now checks that indexed conversation files are present in the package before overwriting destinations. After copying, it resolves conversation indexes against the destination session files, including entries left behind by earlier migrations. SQLite path matching is case-insensitive, respects directory boundaries, and does not replace newly generated paths again. Missing or ambiguous conversation files cause an explicit failure.

Automatic conversation-path repair runs immediately after copying, before other profile conversions. Both the primary index and `sqlite/state_*.sqlite` are checked because Codex Desktop can reconcile the nested index at startup. `HistoryAndTools` includes that database family and the existing reconciliation marker. An already-reconciled legacy entry whose file is unavailable is retained and reported as a warning; missing files in active indexes still fail validation. A final read-only check confirms that stored paths actually point to the destination before restore reports success. The manual repair button is not required after a successful restore.

If files have already been restored but Codex still tries to open another Windows user's session path, close Codex and select **Restore > Repair Conversation Paths**. This operates on the Codex Home shown in Inspect, updates conversation indexes in a SQLite transaction, and does not copy workspaces or rewrite conversation files. It does not recover missing session files. A repair/restore report is saved to `%LOCALAPPDATA%\CodexShuttle\last-restore.log`. If the problem persists after reopening Codex, that report identifies the profile and database actually checked; a different configured database location needs separate investigation.

## Legacy Packages

The package folder name is not part of the file format. A current package may use `CodexShuttle-Current` or another explicitly selected folder name. Compatibility is determined by `manifest.json`, the completion marker, and the checksum file.

Schema 1 packages created by early test builds do not have the current checksum chain and may contain credentials. The app identifies them as legacy and refuses restore. Keep the old package unchanged for recovery purposes, then create a fresh schema 2 backup from the source computer with the current release. Renaming the folder does not upgrade it.

## Safety

- Close Codex before backup or restore so SQLite and session files stop changing.
- Always run Dry Run after changing the package or migration mode.
- Workspace paths are intentionally fixed. A workspace backed up from `E:\CodexWorkspace` restores to `E:\CodexWorkspace` so absolute paths stored in conversations remain valid.
- Mirror restore can delete destination-only files in selected targets.
- A package with an in-progress marker, missing checksum, invalid manifest path, or checksum mismatch is rejected.
- Do not upload backup packages, reports, logs, or local Codex state to GitHub.
- Conversation files can be preserved even when a future Codex UI version does not display every restored task.

Codex stores its normal local state under `CODEX_HOME` (default `~/.codex`). OpenAI documents `auth.json` as containing access tokens, so Codex Shuttle deliberately treats it as a password and excludes it from migration.

## Project Layout

```text
src/
  CodexShuttle.App/       WPF desktop app
  CodexShuttle.Core/      Backup, restore, validation, and migration services
  CodexShuttle.Tests/     Safety and integration tests
docs/
  CodexShuttle_Spec.md    Original product draft and design history
```
