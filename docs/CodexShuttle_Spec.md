# Codex Shuttle 規劃書

> 本文件保留最初產品草案與設計歷史。現行實作以 README 與 schema 2 為準：固定差異封包、changed-files-only rollback journal、完整 Dry Run、checksum 驗證、固定 workspace 絕對路徑，以及 `HistoryAndTools` / `FullProfile` 遷移模式。使用者已明確取消完整 pre-restore snapshot 設計。

版本：v0.1 Draft

用途：交給 Codex 產生 Windows App 之開發規格

目標平台：Windows 10 / 11

建議技術：C# / .NET 8 / WPF

---

## 1. 專案名稱

**Codex Shuttle**

副標題：

> Backup and transfer helper for local Codex Desktop workspaces

說明：

> Codex Shuttle 是一個非官方 Windows 工具，用於備份、驗證、還原與搬移 Codex Desktop 的本機工作狀態，特別適合公司電腦與家用電腦輪流使用，或 Windows 重裝後恢復 Codex 工作內容。

---

## 2. 專案背景

Codex Desktop 的工作內容與 ChatGPT 網頁版不同。ChatGPT 的對話通常在雲端帳號中保存，但 Codex Desktop 的工作狀態、session history、專案路徑、local state、索引與部分設定可能存在本機目錄中。

因此使用者會遇到以下問題：

1. Windows 重裝後，Codex Desktop 的交談紀錄與工作狀態可能消失。
2. 公司電腦與家裡電腦無法自然接續同一個 Codex 工作內容。
3. 使用者不確定應該備份哪些資料夾。
4. 使用 robocopy `/MIR` 容易搞錯方向，導致目的端資料被刪除。
5. Codex 原始 session 檔可能還在，但 Desktop UI 不一定顯示。
6. 使用者需要一個簡單、安全、有防呆、有驗證的 Codex 備份與還原工具。

---

## 3. 專案目標

第一版目標是建立一個 Windows 桌面工具，讓使用者可以：

1. 自動偵測 Codex Desktop / CLI 的本機資料位置。
2. 指定多個工作目錄，例如：
   - `E:\app`
   - `E:\doc`
   - `E:\CodexWorkspace`
3. 一鍵備份 `.codex` 與工作目錄。
4. 產生備份 manifest 與報告。
5. 備份後驗證 Codex session 是否存在。
6. 還原前提供 Dry Run。
7. 還原前自動建立 pre-restore snapshot。
8. 還原時使用 mirror copy，但要清楚提示會覆蓋與刪除目的端多餘檔案。
9. 避免 Codex Desktop 正在執行時進行備份或還原。
10. 在交談 UI 無法恢復時，至少保留原始 session 資料與專案資料。

---

## 4. 非目標

第一版不做以下功能：

1. 不做即時雙向同步。
2. 不做 OneDrive / Google Drive 直接同步 `.codex`。
3. 不嘗試自動合併兩台電腦的 SQLite 或 session index。
4. 不保證 Codex Desktop UI 一定能顯示所有歷史交談。
5. 不支援 macOS / Linux。
6. 不做雲端備份服務。
7. 不備份或顯示 OpenAI token 明文。
8. 不承諾能恢復登入狀態，還原後使用者可能需要重新登入 Codex Desktop。

---

## 5. 使用場景

### 5.1 公司到家裡接續工作

使用者在公司電腦使用 Codex Desktop 開發到一半，準備週末帶回家繼續。

流程：

1. 關閉 Codex Desktop。
2. 使用 Codex Shuttle 建立 `office-to-home` 備份。
3. 將備份放到外接 SSD。
4. 在家裡電腦使用 Codex Shuttle 還原。
5. 開啟 Codex Desktop 繼續工作。

### 5.2 家裡到公司接續工作

週末在家完成部分工作後，週一回公司接續。

流程：

1. 關閉 Codex Desktop。
2. 使用 Codex Shuttle 建立 `home-to-office` 備份。
3. 帶回公司。
4. 在公司電腦還原。
5. 開啟 Codex Desktop 繼續工作。

### 5.3 Windows 重裝前備份

使用者要重裝 Windows。

流程：

1. 關閉 Codex Desktop。
2. 備份 `.codex`、工作目錄、可選 AppData。
3. 重裝 Windows。
4. 安裝 Codex Desktop。
5. 關閉 Codex Desktop。
6. 還原 `.codex` 與工作目錄。
7. 開啟 Codex Desktop。
8. 必要時重新登入。

---

## 6. 預設同步目錄

第一版預設支援以下工作目錄，使用者可修改：

```text
E:\app
E:\doc
E:\CodexWorkspace
```

Codex 本機資料預設位置：

```text
%USERPROFILE%\.codex
```

可選 AppData 位置：

```text
%APPDATA%\Codex
%LOCALAPPDATA%\Codex
%APPDATA%\OpenAI
%LOCALAPPDATA%\OpenAI
```

第一版建議：

- `.codex`：預設備份與還原。
- workspace folders：預設備份與還原。
- AppData：預設備份，但還原時預設不勾選。

---

## 7. 主要備份內容

### 7.1 必備

```text
%USERPROFILE%\.codex
E:\app
E:\doc
E:\CodexWorkspace
```

### 7.2 可選

```text
%APPDATA%\Codex
%LOCALAPPDATA%\Codex
%APPDATA%\OpenAI
%LOCALAPPDATA%\OpenAI
```

### 7.3 `.codex` 內應特別檢查的內容

```text
.codex\sessions\
.codex\archived_sessions\
.codex\state_5.sqlite
.codex\session_index.jsonl
.codex\.codex-global-state.json
.codex\config.toml
```

檔名不應 hard-code 成唯一標準，因為 Codex 版本可能改變。程式應容許這些檔案不存在，但要在 report 中顯示狀態。

---

## 8. 備份封包結構

備份目的地範例：

```text
F:\CodexTransfer\office-to-home
```

封包結構：

```text
office-to-home\
  manifest.json
  backup-report.txt
  checksums.sha256
  .codex\
  workspaces\
    E_app\
    E_doc\
    E_CodexWorkspace\
  appdata_optional\
    C_Users_xxx_AppData_Roaming_Codex\
    C_Users_xxx_AppData_Local_Codex\
    C_Users_xxx_AppData_Roaming_OpenAI\
    C_Users_xxx_AppData_Local_OpenAI\
```

---

## 9. manifest.json 規格

範例：

```json
{
  "schemaVersion": 1,
  "appName": "Codex Shuttle",
  "appVersion": "0.1.0",
  "createdAt": "2026-06-30T18:20:00+08:00",
  "sourceComputer": "OFFICE-PC",
  "sourceUser": "Charlie",
  "sourceUserProfile": "C:\\Users\\Charlie",
  "codexHome": "C:\\Users\\Charlie\\.codex",
  "backupRoot": "F:\\CodexTransfer\\office-to-home",
  "workspacePaths": [
    {
      "sourcePath": "E:\\app",
      "packagePath": "workspaces\\E_app",
      "enabled": true
    },
    {
      "sourcePath": "E:\\doc",
      "packagePath": "workspaces\\E_doc",
      "enabled": true
    },
    {
      "sourcePath": "E:\\CodexWorkspace",
      "packagePath": "workspaces\\E_CodexWorkspace",
      "enabled": true
    }
  ],
  "appDataPaths": [
    {
      "sourcePath": "%APPDATA%\\Codex",
      "packagePath": "appdata_optional\\AppData_Roaming_Codex",
      "exists": false,
      "included": false
    }
  ],
  "codexInspection": {
    "codexHomeExists": true,
    "sessionsFolderExists": true,
    "sessionsFileCount": 128,
    "archivedSessionsFileCount": 12,
    "latestSessionWriteTime": "2026-06-30T17:42:00+08:00",
    "stateSqliteExists": true,
    "stateSqliteReadable": true,
    "sessionIndexExists": true,
    "sessionIndexReadable": true,
    "globalStateExists": true,
    "globalStateJsonValid": true,
    "configTomlExists": true
  },
  "copyMode": "mirror",
  "checksumFile": "checksums.sha256"
}
```

---

## 10. 核心功能

### 10.1 自動偵測

啟動時自動偵測：

1. `%USERPROFILE%\.codex`
2. `E:\app`
3. `E:\doc`
4. `E:\CodexWorkspace`
5. AppData 中 Codex / OpenAI 相關資料夾

顯示：

- 是否存在。
- 檔案數。
- 資料夾大小。
- 最近修改時間。
- `.codex` session 數量。
- SQLite 是否可讀。
- JSON / JSONL 是否可讀。

---

### 10.2 Codex 程序檢查

備份與還原前，檢查以下程序：

```text
Codex.exe
codex.exe
```

若正在執行，提示：

```text
Codex Desktop is currently running.
Please close Codex before backup or restore.

[Close automatically] [Cancel]
```

若使用者選擇自動關閉，使用 Process Kill。

---

### 10.3 備份功能

備份步驟：

1. 檢查 Codex 是否正在執行。
2. 掃描來源資料夾。
3. 建立備份目的地。
4. 複製 `.codex`。
5. 複製 workspace folders。
6. 可選複製 AppData。
7. 產生 `manifest.json`。
8. 產生 `backup-report.txt`。
9. 產生 `checksums.sha256`。
10. 驗證備份結果。

備份模式：

- 預設 mirror copy。
- 來源不存在時不報錯，但要在 report 中列出 skipped。
- 檔案複製失敗時，要列出失敗檔案與原因。

---

### 10.4 還原功能

還原步驟：

1. 選擇備份封包。
2. 讀取 `manifest.json`。
3. 顯示來源電腦、備份時間、來源路徑。
4. 檢查 Codex 是否正在執行。
5. 執行 Dry Run。
6. 顯示會覆蓋、會新增、會刪除的摘要。
7. 建立 pre-restore snapshot。
8. 還原 `.codex`。
9. 還原 workspace folders。
10. AppData 預設不還原，除非使用者手動勾選。
11. 產生 restore report。

---

### 10.5 Dry Run

Dry Run 不修改任何檔案。

Dry Run 顯示：

```text
Will restore:
- .codex
- E:\app
- E:\doc
- E:\CodexWorkspace

Will overwrite:
- 342 files

Will delete because mirror mode:
- 12 files

Will add:
- 128 files

Estimated data:
- 15.4 GB
- 62,120 files
```

Dry Run 必須在真正還原前執行，或至少提供等效摘要。

---

### 10.6 Pre-restore Snapshot

還原前自動備份目的端現有資料。

範例：

```text
F:\CodexTransfer\PreRestore-HOME-PC-20260630-2100\
  .codex\
  workspaces\
    E_app\
    E_doc\
    E_CodexWorkspace\
  manifest-before-restore.json
```

若還原方向搞錯，使用者可用 snapshot 救回。

選項：

```text
[✓] Create pre-restore snapshot before restore
```

第一版應預設啟用，不建議關閉。

---

### 10.7 備份驗證

備份後檢查：

1. `.codex` 是否存在。
2. `.codex\sessions` 是否存在。
3. session 檔案數量。
4. 最近 session 修改時間。
5. `state_5.sqlite` 是否存在。
6. 若存在，嘗試使用 SQLite 開啟。
7. `session_index.jsonl` 是否存在。
8. 若存在，嘗試逐行讀取前幾行 JSON。
9. `.codex-global-state.json` 是否有效 JSON。
10. checksum 是否產生成功。

---

## 11. UI 規劃

### 11.1 主畫面

```text
Codex Shuttle

[Backup] [Restore] [Inspect] [Settings]
```

---

### 11.2 Backup 頁面

顯示：

```text
Codex Home:
  C:\Users\Charlie\.codex
  Status: Detected
  Sessions: 128
  Latest session: 2026-06-30 17:42
  SQLite state: OK

Workspaces:
  [✓] E:\app
  [✓] E:\doc
  [✓] E:\CodexWorkspace
  [+ Add Folder]

Optional AppData:
  [ ] %APPDATA%\Codex
  [ ] %LOCALAPPDATA%\Codex
  [ ] %APPDATA%\OpenAI
  [ ] %LOCALAPPDATA%\OpenAI

Destination:
  F:\CodexTransfer\office-to-home
  [Browse]

Options:
  [✓] Stop Codex before backup
  [✓] Generate checksums
  [✓] Generate manifest
  [✓] Generate backup report

[Create Backup]
```

---

### 11.3 Restore 頁面

顯示：

```text
Restore Package:
  F:\CodexTransfer\office-to-home
  [Browse]

Package Info:
  Created: 2026-06-30 18:20
  Source PC: OFFICE-PC
  Source User: Charlie

Restore Targets:
  [✓] .codex → C:\Users\Charlie\.codex
  [✓] E_app → E:\app
  [✓] E_doc → E:\doc
  [✓] E_CodexWorkspace → E:\CodexWorkspace
  [ ] AppData optional

Safety:
  [✓] Verify checksums before restore
  [✓] Create pre-restore snapshot
  [✓] Stop Codex before restore

[Dry Run]
[Restore]
```

---

### 11.4 Inspect 頁面

Inspect 用來只檢查目前本機 Codex 狀態，不備份也不還原。

顯示：

```text
Codex Home:
  C:\Users\Charlie\.codex

Detected files:
  sessions folder: OK
  sessions count: 128
  archived sessions count: 12
  state_5.sqlite: OK
  session_index.jsonl: OK
  .codex-global-state.json: OK
  config.toml: OK

Latest sessions:
  2026-06-30 17:42
  2026-06-30 16:11
  2026-06-29 21:30
```

---

## 12. 技術架構建議

### 12.1 開發平台

建議：

```text
C# / .NET 8 / WPF
```

原因：

1. Windows 原生。
2. 容易處理檔案、程序、路徑、權限。
3. SQLite、JSON、ZIP、SHA256 library 成熟。
4. 方便做 GUI。
5. 企業內部部署接受度高。

---

### 12.2 專案架構

建議專案結構：

```text
CodexShuttle/
  src/
    CodexShuttle.App/
      App.xaml
      MainWindow.xaml
      Views/
      ViewModels/
    CodexShuttle.Core/
      Models/
      Services/
      Utilities/
    CodexShuttle.Tests/
  docs/
    CodexShuttle_Spec.md
  README.md
```

---

### 12.3 核心服務類別

建議類別：

```text
CodexDetector
BackupService
RestoreService
DryRunService
SnapshotService
ManifestService
ChecksumService
CodexProcessService
CodexInspector
FileMirrorService
ReportService
SettingsService
```

---

## 13. 主要資料模型

### 13.1 BackupManifest

```csharp
public class BackupManifest
{
    public int SchemaVersion { get; set; }
    public string AppName { get; set; } = "Codex Shuttle";
    public string AppVersion { get; set; } = "0.1.0";
    public DateTimeOffset CreatedAt { get; set; }
    public string SourceComputer { get; set; } = string.Empty;
    public string SourceUser { get; set; } = string.Empty;
    public string SourceUserProfile { get; set; } = string.Empty;
    public string CodexHome { get; set; } = string.Empty;
    public string BackupRoot { get; set; } = string.Empty;
    public List<WorkspaceEntry> WorkspacePaths { get; set; } = new();
    public List<AppDataEntry> AppDataPaths { get; set; } = new();
    public CodexInspectionResult CodexInspection { get; set; } = new();
    public string CopyMode { get; set; } = "mirror";
    public string ChecksumFile { get; set; } = "checksums.sha256";
}
```

### 13.2 WorkspaceEntry

```csharp
public class WorkspaceEntry
{
    public string SourcePath { get; set; } = string.Empty;
    public string PackagePath { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public bool Exists { get; set; }
    public long FileCount { get; set; }
    public long TotalBytes { get; set; }
}
```

### 13.3 AppDataEntry

```csharp
public class AppDataEntry
{
    public string SourcePath { get; set; } = string.Empty;
    public string PackagePath { get; set; } = string.Empty;
    public bool Exists { get; set; }
    public bool Included { get; set; }
}
```

### 13.4 CodexInspectionResult

```csharp
public class CodexInspectionResult
{
    public bool CodexHomeExists { get; set; }
    public bool SessionsFolderExists { get; set; }
    public int SessionsFileCount { get; set; }
    public int ArchivedSessionsFileCount { get; set; }
    public DateTimeOffset? LatestSessionWriteTime { get; set; }

    public bool StateSqliteExists { get; set; }
    public bool StateSqliteReadable { get; set; }

    public bool SessionIndexExists { get; set; }
    public bool SessionIndexReadable { get; set; }

    public bool GlobalStateExists { get; set; }
    public bool GlobalStateJsonValid { get; set; }

    public bool ConfigTomlExists { get; set; }

    public List<string> Warnings { get; set; } = new();
}
```

### 13.5 DryRunResult

```csharp
public class DryRunResult
{
    public long FilesToCopy { get; set; }
    public long FilesToOverwrite { get; set; }
    public long FilesToDelete { get; set; }
    public long TotalBytesToCopy { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<FileOperationPreview> Operations { get; set; } = new();
}
```

---

## 14. Copy 實作策略

第一版可以有兩種選項。

### 選項 A：呼叫 robocopy

優點：

1. 穩定。
2. Windows 內建。
3. 適合大量檔案。
4. 支援 mirror。
5. 支援 retry。

缺點：

1. 解析輸出較麻煩。
2. exit code 需要特別處理，robocopy 的 exit code 不等於一般程式錯誤碼。

### 選項 B：使用 .NET 自行複製

優點：

1. 更容易整合 UI 進度。
2. 可完整控制 Dry Run。
3. 可跨平台演進。

缺點：

1. mirror、ACL、長路徑、失敗重試要自己處理。
2. 大量檔案效能可能不如 robocopy。

建議 MVP：

```text
第一版先使用 robocopy 執行實際 copy。
Dry Run 可先用 .NET 掃描來源與目的端差異。
```

robocopy 參數建議：

```text
/MIR /R:1 /W:1 /XJ /FFT
```

說明：

- `/MIR`：鏡像同步。
- `/R:1`：失敗重試一次。
- `/W:1`：重試等待一秒。
- `/XJ`：避免 junction 造成循環。
- `/FFT`：較寬鬆的檔案時間比較，對不同磁碟較安全。

Robocopy exit code 注意：

```text
0: No files copied, no failure.
1: Files copied successfully.
2: Extra files or directories detected.
3: Files copied and extra files detected.
4: Mismatched files or directories detected.
5-7: Mixture of copy and mismatch conditions, usually not fatal depending on context.
8 or above: At least one failure occurred.
```

程式不應把所有非 0 exit code 都視為失敗。一般建議 `exitCode >= 8` 才視為失敗。

---

## 15. 安全與防呆要求

### 15.1 還原方向確認

還原前必須顯示明確文字：

```text
You are restoring backup from:
OFFICE-PC
Created at: 2026-06-30 18:20

To this computer:
HOME-PC

The following folders will be overwritten:
- C:\Users\Charlie\.codex
- E:\app
- E:\doc
- E:\CodexWorkspace
```

使用者必須按下確認。

---

### 15.2 Mirror Delete 警告

因為 mirror copy 會刪除目的端多餘檔案，所以 UI 要顯示：

```text
Warning:
Mirror restore will make the destination folders exactly match the backup.
Files that exist only in the destination may be deleted.
```

---

### 15.3 Pre-restore Snapshot 預設開啟

除非使用者明確關閉，否則還原前必須先建立 snapshot。

---

### 15.4 不顯示敏感資訊

不要在 UI、report、log 中顯示：

1. OpenAI access token。
2. API key。
3. credential 明文。
4. auth 檔內容。

可以顯示檔案存在與大小，但不要 dump 內容。

---

## 16. Report 格式

### 16.1 backup-report.txt 範例

```text
Codex Shuttle Backup Report
===========================

Backup time: 2026-06-30 18:20
Source computer: OFFICE-PC
Source user: Charlie

Codex Home:
  Path: C:\Users\Charlie\.codex
  Exists: Yes
  Sessions: 128
  Archived sessions: 12
  Latest session: 2026-06-30 17:42
  state_5.sqlite: Exists, Readable
  session_index.jsonl: Exists, Readable
  .codex-global-state.json: Exists, Valid JSON
  config.toml: Exists

Workspaces:
  E:\app
    Files: 10240
    Size: 3.2 GB
    Status: Backed up

  E:\doc
    Files: 5420
    Size: 1.1 GB
    Status: Backed up

  E:\CodexWorkspace
    Files: 18320
    Size: 8.7 GB
    Status: Backed up

Warnings:
  None
```

---

## 17. 進階功能規劃

以下功能不是 MVP，但未來很有價值。

### 17.1 Export Codex Conversations to Markdown

讀取 `.codex\sessions\*.jsonl`，匯出成：

```text
codex-history-export\
  ProjectA\
    2026-06-30-thread-001.md
    2026-06-30-thread-002.md
```

用途：

1. 即使 Codex Desktop UI 不顯示，使用者仍能閱讀交談。
2. 可放入 Git 作為 audit trail。
3. 可讓新 Codex thread 讀取舊工作內容。

---

### 17.2 Handoff Summary Helper

工具可在 workspace 中建立 template：

```text
docs\codex\handoff-current.md
```

內容：

```markdown
# Codex Handoff Summary

## Current Goal

## Completed Work

## Modified Files

## Pending Work

## Known Bugs

## Design Decisions

## Test Status

## Next Steps
```

---

### 17.3 History Repair

分析 `.codex` 的 session、index、global state、workspace root，未來嘗試修復：

1. session 在檔案中存在，但 UI 不顯示。
2. workspace path 變更。
3. session index 遺失或損壞。
4. global state JSON 損壞。

第一版不要做自動修復，只做檢查與報告。

---

## 18. MVP 開發順序

### Phase 1：Core CLI / Service

先不做完整 UI，先做核心功能。

功能：

1. Detect Codex home。
2. Inspect `.codex`。
3. Backup `.codex`。
4. Backup workspace folders。
5. Generate manifest。
6. Generate report。
7. Check Codex process。
8. Basic restore。
9. Pre-restore snapshot。

---

### Phase 2：WPF GUI

功能：

1. Backup page。
2. Restore page。
3. Inspect page。
4. Settings page。
5. Progress display。
6. Dry Run display。

---

### Phase 3：Verification

功能：

1. SHA256 checksum。
2. SQLite readable check。
3. JSON / JSONL readable check。
4. Restore verification。

---

### Phase 4：Markdown Export

功能：

1. Read session JSONL。
2. Export conversations to Markdown。
3. Group by workspace or session date。

---

### Phase 5：History Repair

功能：

1. Analyze broken state。
2. Detect missing index。
3. Suggest path mapping。
4. Optionally rebuild partial index。

---

## 19. Codex 開發起始 Prompt

可以直接把以下內容貼給 Codex：

```text
請幫我建立一個 Windows C# .NET 8 WPF 工具，名稱 Codex Shuttle。

用途：
備份與還原 Codex Desktop 的本機工作狀態，讓使用者可以在公司電腦與家裡電腦之間單向搬移工作內容，或在 Windows 重裝後恢復 Codex 工作資料。

第一版目標：
1. 偵測 %USERPROFILE%\.codex。
2. 偵測預設 workspace folders：
   - E:\app
   - E:\doc
   - E:\CodexWorkspace
3. 讓使用者新增或移除 workspace folders。
4. 備份 .codex 與 workspace folders 到指定 backup root。
5. 可選備份 AppData：
   - %APPDATA%\Codex
   - %LOCALAPPDATA%\Codex
   - %APPDATA%\OpenAI
   - %LOCALAPPDATA%\OpenAI
6. 備份前檢查 Codex.exe / codex.exe 是否正在執行，並提示使用者關閉。
7. 備份後產生 manifest.json。
8. 備份後產生 backup-report.txt。
9. 檢查 .codex\sessions、archived_sessions、state_5.sqlite、session_index.jsonl、.codex-global-state.json、config.toml 是否存在。
10. 嘗試檢查 state_5.sqlite 是否可讀。
11. 嘗試檢查 .codex-global-state.json 是否為有效 JSON。
12. 還原前支援 Dry Run。
13. 還原前自動建立 pre-restore snapshot。
14. 還原使用 mirror copy，但 UI 必須清楚警告目的端多餘檔案可能被刪除。
15. AppData 預設備份但不預設還原。
16. 不要顯示或輸出任何 token / API key / credential 明文。

技術要求：
- 使用 C# .NET 8。
- 使用 WPF。
- 專案分成 CodexShuttle.App、CodexShuttle.Core、CodexShuttle.Tests。
- Core service 類別至少包含：
  - CodexDetector
  - CodexInspector
  - CodexProcessService
  - BackupService
  - RestoreService
  - DryRunService
  - SnapshotService
  - ManifestService
  - ReportService
  - FileMirrorService
- 實際檔案複製第一版可使用 robocopy。
- Dry Run 可用 .NET 掃描來源與目的端差異。
- robocopy 參數建議使用 /MIR /R:1 /W:1 /XJ /FFT。
- 需要處理 robocopy exit code，不要把所有非 0 exit code 都視為失敗。

請先建立完整專案架構、資料模型、核心服務 skeleton、基本 WPF 主畫面與 Backup / Restore / Inspect 頁面，不要一次實作所有細節。完成後請列出下一步 TODO。
```

---

## 20. 驗收標準

MVP 完成時，至少要能做到：

1. 開啟程式後可偵測 `%USERPROFILE%\.codex`。
2. 可顯示 session 數量。
3. 可顯示 `state_5.sqlite` 是否存在。
4. 可顯示 `session_index.jsonl` 是否存在。
5. 可顯示 `.codex-global-state.json` 是否存在且 JSON 是否有效。
6. 可設定備份目的地。
7. 可備份 `.codex`。
8. 可備份 `E:\app`、`E:\doc`、`E:\CodexWorkspace`。
9. 可產生 `manifest.json`。
10. 可產生 `backup-report.txt`。
11. 還原前可執行 Dry Run。
12. 還原前可建立 pre-restore snapshot。
13. 還原時可把資料覆蓋回原路徑。
14. Codex 正在執行時會警告並停止備份 / 還原。
15. 錯誤時有明確訊息，不會靜默失敗。

---

## 21. 重要注意事項

1. `.codex` 內部結構可能隨 Codex Desktop 版本改變，因此程式不可假設所有檔案都一定存在。
2. 不應保證還原後 Codex Desktop UI 100% 顯示所有舊交談。
3. 工具目標是最大化資料保留與降低搬移風險。
4. 專案資料夾與 Git commit 仍應視為主要工作備份。
5. `.codex` 備份是保留 Codex session history 與 local state 的輔助方式。
6. 還原 AppData 可能影響登入或 UI cache，因此第一版預設不還原 AppData。
7. 使用 mirror restore 時，目的端多餘檔案會被刪除，因此必須有 Dry Run 與 pre-restore snapshot。
8. 不支援兩台電腦同時修改後合併 `.codex`。
9. 適合單向搬移，例如公司到家裡、家裡到公司。
10. 若要支援雙向合併，必須另做完整 merge design，不列入 MVP。

---

## 22. README 摘要建議

```markdown
# Codex Shuttle

Codex Shuttle is an unofficial Windows backup and transfer helper for local Codex Desktop workspaces.

It helps users safely move Codex Desktop local state and project folders between computers, or restore them after reinstalling Windows.

## Features

- Detect `%USERPROFILE%\.codex`
- Backup Codex sessions and local state
- Backup selected workspace folders
- Generate backup manifest
- Generate backup report
- Verify session files and state database presence
- Dry run before restore
- Pre-restore snapshot
- Robocopy-based mirror restore
- Optional AppData backup

## Warning

Codex Shuttle does not guarantee that Codex Desktop UI will always display restored conversations. It preserves local files and workspace data as safely as possible.

Always keep Git commits and project handoff summaries as an additional backup.
```

---

## 23. 建議第一輪交給 Codex 的工作範圍

第一輪請 Codex 只做以下事情，避免範圍太大：

1. 建立 solution 與三個專案：
   - `CodexShuttle.App`
   - `CodexShuttle.Core`
   - `CodexShuttle.Tests`
2. 建立資料模型。
3. 建立 service skeleton。
4. 實作 `CodexDetector`。
5. 實作 `CodexInspector` 的基本檢查。
6. 建立 WPF 主畫面。
7. 建立 Backup / Restore / Inspect 三個基本頁面。
8. 暫時不要實作 history repair。
9. 暫時不要實作 conversation markdown export。
10. 完成後列出 TODO。
