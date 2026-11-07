# 2026-11-07 Recovery Handoff: Preview.13

This is the durable engineering record for changes developed after commit `20e74d9` (preview.4), through `0.3.0-preview.13`. Read this document first after replacing a local workspace from an older backup. It records implementation decisions and remaining limitations; it is not a copy of private Codex conversations.

## Recovery Boundary

- Repository: `https://github.com/uart-aplex/CodexShuttle.git`, branch `main`.
- Application version: `0.3.0-preview.13` in `src/CodexShuttle.App/CodexShuttle.App.csproj`.
- Disaster Recovery can replace this checkout and its `.git` directory when its containing workspace is selected. GitHub commits are independent of that local replacement.
- A source-code push preserves committed code, tests and documentation. It does not preserve local Codex chats, credentials, private projects, USB packages, restore points or ignored executables.
- Portable binaries and a source recovery copy were also placed outside the E: workspace, under `%USERPROFILE%\CodexShuttle-Safety\CodexShuttle-v0.3.0-preview.13`. Do not include this safety folder in replacement restore targets. This is a source/executable recovery folder, not a complete Codex backup.

## Change Inventory

| Preview | Problem | Implemented behavior | Main code and tests |
| --- | --- | --- | --- |
| .5 | A copy failure returned before automatic conversation-path repair; localized Robocopy details were lost | Check running executables in replacement targets, retain localized copy errors and summaries, and attempt limited path recovery only after complete matching history verification. A partial restore remains a failure | `RestoreProcessGuard`, `PartialRestorePathRecovery`, `RobocopyDiagnostics`, `RestoreService`; `PartialRestoreRecoveryTests`, `FileMirrorServiceTests` |
| .6 | Disaster Recovery Dry Run appeared stalled with an empty log | Report target/source/destination scans, sampled file paths, counts and terminal outcomes; support cancellation and persistent Dry Run journals | `DryRunService`, `MainWindowViewModel`; `DryRunProgressTests` |
| .8 | Modern paginated/compressed histories and dormant nested indexes blocked all imports | Support the reviewed primary state/history schema, compressed rollout bytes and matching projection offsets; use existing reconciliation markers without fabricating them; snapshot and recover both metadata databases | `MergeHistory`, `MergeDatabase`, `MergeRestoreService`; `PaginatedMergeTests` |
| .9 | Old unchanged conversations were classified as conflicts after migration | Separate exact storage hashes from logical comparison. Normalize known user/workspace paths, JSON formatting, compression and settings-only events. Equivalent conversations remain untouched | `MergeComparison`, `MergeHistory`; `MergeComparisonTests` |
| .10 | A completed operation retained conflicts but added no visible conversations; checkbox edits invalidated approval | Show actual selected-addition/conflict/skip counts, preserve terminal errors, keep selection changes within the approved plan, and provide explicit selection of eligible additions | `MergeViewModel`, `MergeModels`, `MainWindow.xaml`; `MergeViewModelTests` |
| .11 | A new conversation could not import because its project directory was absent | Add missing files/directories from enabled backup workspaces before importing chats, verify each new file and retain existing/local-only files; cancellation/retry keeps completed additions | `MergeWorkspaces`, `MergeRestoreService`; `MergeWorkspaceTests`, native paginated import test |
| .12 | Same-ID source continuations were retained only as external conflicts, so new messages were not available in Codex | Verify ordered rollout and projection ancestry; add `Update` and `LocalAhead`; preserve original revisions and local thread metadata; journal updated IDs for guarded recovery | `MergeComparison`, `MergeHistory`, `MergeDatabase`, `MergeRestoreService`; `IncrementalMergeTests` |
| .13 | A surviving Chrome extension host blocked the entire replacement restore | Keep the recognized active Chrome cache locally, exclude it from mirror deletion/copy/path remapping, report the exception, and continue restoring other data; unknown running executables remain guarded | `RestoreProcessGuard`, `FileMirrorOptions`, `DryRunService`, `ProfilePathRemapService`, `RestoreService`; `PartialRestoreRecoveryTests` |

## Why These Rules Matter

### Restore, Merge And Backup Are Different

Disaster Recovery is replacement restore. Both FullProfile and HistoryAndTools may overwrite selected local history and mirror-delete destination-only files. HistoryAndTools preserving login/provider settings does not preserve independent local conversations.

Merge Restore is not routed through mirror copying. It imports selected missing chats, activates verified same-ID continuations, and optionally supplies missing project files. It does not replace existing project files, even when the backup contains a newer version. Use the project's version-control workflow or separately reviewed file reconciliation for those changes.

Backup currently updates a mirror-style package by difference. It is not a union/append operation. Reusing the only incoming package for an ordinary backup can replace or remove data that exists only in that package.

### Same-ID History Decisions

- `Add`: no conflicting local ID/path/metadata; import a new conversation.
- `Identical` / `Equivalent`: same history, possibly with supported migration representation differences; no write.
- `Update`: all local records are a verified prefix of the incoming rollout, and compared projection tables are equal or source-side extensions; activate the incoming continuation under the same ID.
- `LocalAhead`: the incoming history is already included in the more complete local history; no write.
- `Conflict`: both sides have different continuations, an earlier record changed, or rollout/projection comparisons disagree. Keep local and, when selected, save the incoming revision outside Codex. This retained file is not a visible/resumable branch in Desktop.
- `Blocked`: unsupported layout, inconsistent metadata, missing project data, occupied destination or other safety failure. Do not delete an index or bypass checks to make it import.

Example: shared turns 1-10, office adds its own 11-15, home adds different 11-13. This remains a conflict. Preview.12 only resolves verified one-sided continuation, not general two-sided message splicing.

### Cross-Computer Paths And Paginated History

Different Windows usernames remain supported by profile mapping. Shared project paths can stay fixed across computers. USB sources resolve beneath the selected package root rather than the previous machine's drive letter.

Paginated imports preserve the incoming rollout bytes and projection offsets as a unit. Compressed physical `.jsonl.zst` files keep logical `.jsonl` references. Updates write a new revision under `sessions/shuttle-revisions` and retain the previous file rather than overwriting it in place. Local title, project/parent links, pinning, archive state and provider metadata remain local. Do not rewrite compressed transcript bytes independently of the projection database.

Restore points are local metadata/index snapshots and a journal of owned new files; they are not full workspace copies. Rollback verifies before/after fingerprints, restores updated thread rows and projection rows, and handles partial commits of attached databases independently. Newer local changes block rollback. Completed additive workspace files are deliberately not removed by conversation rollback.

### Active Chrome Cache Exception

The recognized host is under the destination profile's `plugins/cache/openai-bundled/chrome/<version>/extension-host/windows/{x64,arm64}/extension-host.exe`. Only recognizing the filename is insufficient. The exception retains the Chrome cache, not arbitrary plugins or the entire profile.

If the package lacks an ancestor of that cache, preserve the highest missing subtree too. Real Robocopy tests demonstrated that a deeper exclusion alone can fail to protect files when an absent ancestor is recursively purged. Dry Run, copy exclusions and path remapping use the same subtree semantics. The log lists retained paths and the final result says the active cache was not restored. Codex itself must still close; no browser process is killed automatically.

## Verification At Handoff

- Standard final suite: **163 core tests + 10 WPF tests passed**, five opt-in cases skipped.
- Focused final restore/process regression group: **20 tests passed**, including real Robocopy transfers with locked synthetic executables in both restore modes.
- At preview.12, two opt-in isolated native Codex app-server tests passed: compressed new-chat import with missing mapped workspace, and same-ID continuation import/read/resume while keeping another chat readable. No model requests were made. These were not rerun as part of the final preview.13 standard suite.
- Sixteen offscreen WPF layout renders passed at four viewport sizes during preview.12. General isolated Desktop GUI acceptance is still pending; app-server tests are not equivalent to full GUI acceptance.
- Portable Windows x64 publication succeeded. `git diff --check` passed before publication.
- Tests use isolated temporary profiles. Production chats, USB packages and project files were not restored or merged while implementing these changes. Read-only incident diagnostics are excluded from Git.

## Not Implemented

- Selecting local conversations and appending them safely into an existing USB backup package.
- Turning a two-sided conflict into a visible, independently resumable Codex branch, or combining both branches automatically.
- Overwriting/merging existing workspace files during Merge Restore.
- Merging active-profile skills/plugin settings/provider configuration during Merge Restore. Backup/Disaster Recovery have broader migration scope.
- Automatic synchronization, cloud transport/encryption, general adapters for unknown database generations, or completed Desktop GUI acceptance.

Do not describe any of these as available after fetching this commit. In particular, selective backup append was discussed but neither implemented nor run against the USB package.

## Recover After Replacement Restore

1. Open this document on GitHub before trusting a restored local copy. An older backup may contain the old code and executable.
2. Inspect the local checkout with `git status --short --branch` and `git remote -v`.
3. If clean and pointing at the intended repository, use the commands below. A non-fast-forward result is a reason to preserve and inspect the checkout, not force-reset it.

```powershell
Set-Location 'E:\CodexWorkspace\SoftwareDevelopment\CodexShuttle'
git fetch origin
git switch main
git merge --ff-only origin/main
git log -1 --oneline
Select-String -Path 'src\CodexShuttle.App\CodexShuttle.App.csproj' -Pattern '<Version>'
```

If the checkout is dirty, diverged, missing Git metadata or has the wrong remote, keep it untouched and clone into a new empty recovery directory outside replacement targets:

```powershell
$recovery = Join-Path $env:USERPROFILE 'CodexShuttle-Recovered-20261107'
if (Test-Path -LiteralPath $recovery) { throw 'Choose a different new recovery directory; do not overwrite this one.' }
git clone https://github.com/uart-aplex/CodexShuttle.git $recovery
Set-Location $recovery
git log -1 --oneline
```

Then read this file plus `docs/restore-safety-and-handoff.md`. Verify that the app version is preview.13 or a deliberately reviewed later version. Run the tests and rebuild with an installed .NET 8 SDK:

```powershell
dotnet test CodexShuttle.sln -c Release
dotnet publish src/CodexShuttle.App/CodexShuttle.App.csproj -p:PublishProfile=Portable-win-x64
```

On the development computer, the SDK was available at `E:\tools\.dotnet-sdk-8.0.422\dotnet.exe`; a runtime-only installation under Program Files is not enough to build. Prefer the existing SDK instead of downloading a replacement unnecessarily.

The published executable is `artifacts\portable-win-x64\CodexShuttle.App.exe`. Fetching Git does not update ignored executables in `outputs`, safety folders or USB packages. Use a newly built executable or the separately protected preview.13 binary. This source push by itself does not create a GitHub Release or upload that binary.

## Continue Development In A New Chat

Point the new coding session to this repository and ask it to read, in order:

1. `docs/2026-11-07-recovery-handoff.md`
2. `docs/restore-safety-and-handoff.md`
3. `docs/v0.3.0-preview.13-browser-cache-restore.md`
4. `docs/v0.3.0-preview.12-incremental-conversations.md`
5. Relevant tests before modifying restore/merge behavior.

Do not infer completion from an older executable or a backup folder timestamp. Use the remote commit, actual app version and final operation log. Keep logs and real conversations private; only sanitized implementation notes belong in this public repository.
