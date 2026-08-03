# Codex Shuttle

Codex Shuttle is an unofficial Windows backup and migration tool for local Codex Desktop, CLI, and IDE state.

It keeps a reusable differential package for moving conversations, workspaces, skills, plugins, rules, and Codex settings between computers. Backup packages are local artifacts and must never be committed to Git.

## Features

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

Non-developers can download the `CodexShuttle-v0.2.1-win-x64.exe` asset from the
[latest GitHub release](https://github.com/uart-aplex/CodexShuttle/releases/latest). It is a self-contained single-file app and does not require a separate .NET installation or DLL download.

Keep the executable on a local drive or USB drive, close Codex, and then run it. Windows SmartScreen may show an unknown-publisher warning because the project does not currently use a paid code-signing certificate. Verify the downloaded file against `SHA256SUMS.txt` from the same release before running it.

The app stores computer-specific paths at:

```text
%LOCALAPPDATA%\CodexShuttle\settings.json
```

## Different Windows User Names

Windows user-profile paths are mapped to the account running the restore. For example, a backup from `C:\Users\OfficeUser\.codex` restores to the current computer's `CODEX_HOME` or `C:\Users\HomeUser\.codex`. Personal `.agents`, roaming AppData, and local AppData targets are mapped the same way. The source computer's user directory is not created on the destination.

Codex Shuttle does not rewrite arbitrary path strings stored inside conversations, skill files, or configuration content. Keep workspace projects on the same absolute path, such as `E:\CodexWorkspace`, on both computers so those references remain valid.

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
