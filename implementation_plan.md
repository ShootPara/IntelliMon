# Implementation Plan: IntelliMon AI

IntelliMon AI is a lightweight native Windows system-tray utility that monitors running processes for unusual resource consumption (**CPU, Memory, and Disk**), identifies what qualifying processes are doing using rich local metadata and an offline-first SQLite knowledge base, and asynchronously enriches unknown processes via an AI provider (Gemini) using structured JSON output.

IntelliMon AI operates entirely as a standard, unprivileged Windows desktop utility without requiring Administrator elevation.

---

## Technical Evaluation & Findings

Before finalizing the architecture, we evaluated the candidate Windows and .NET APIs across all core dimensions.

### 1. Per-Process Telemetry Mechanisms

| Resource | Candidate APIs | Recommended Mechanism | Key Findings & Technical Approach |
| :--- | :--- | :--- | :--- |
| **CPU** | 1. `Process.TotalProcessorTime`<br>2. `NtQuerySystemInformation`<br>3. `PerformanceCounter` | **`NtQuerySystemInformation` (P/Invoke)** with fallback to BCL | • `PerformanceCounter` has severe name collision issues (`svchost#1`, `svchost#2`) and high overhead.<br>• `Process.GetProcesses()` allocates ~300 managed wrappers and handles per tick, and throws `Win32Exception (Access Denied)` on system/protected processes.<br>• `NtQuerySystemInformation(SystemProcessInformation)` returns all system processes, CPU user/kernel times, thread counts, memory, and I/O counters in a **single sub-millisecond snapshot** without handle leaks or access-denied exceptions. Achieves negligible CPU overhead (<0.1%).<br>• **Threshold**: Percentage of total available CPU capacity (`deltaProcessorTime / (deltaTime * ProcessorCount)`). |
| **Memory** | 1. `Process.WorkingSet64`<br>2. `GetProcessMemoryInfo`<br>3. `SYSTEM_PROCESS_INFORMATION` | **Physical Working Set (`WorkingSetSize`)** as percentage of total RAM | • User-facing threshold and display are strictly **percentage of total physical system RAM**: `(WorkingSetSize / TotalPhysicalMemory) * 100%`.<br>• Total physical RAM is queried once via Win32 `GlobalMemoryStatusEx` (`MEMORYSTATUSEX.ullTotalPhys`).<br>• `WorkingSetSize` is directly available in the `SYSTEM_PROCESS_INFORMATION` snapshot with zero extra Win32 calls.<br>• Private/committed memory (`PrivateWorkingSet` / `PagefileUsage`) is collected internally for diagnostics and AI context, but is not a separate user-facing threshold in v1. |
| **Disk** | 1. `GetProcessIoCounters`<br>2. `SYSTEM_PROCESS_INFORMATION.IoCounters`<br>3. PDH `\PhysicalDisk(*)\% Disk Time` | **Attributed Disk Active Time Percentage** | • **Percentage Semantics**: Windows Task Manager defines disk utilization as **Active Time %** (the percentage of elapsed time the disk drive is actively servicing requests, representing saturation).<br>• **Calculation**: IntelliMon calculates each process's disk percentage as its proportional share of active disk utilization:<br>$$\text{Process Disk \%} = \left(\frac{\text{Process I/O Bytes/sec}}{\sum \text{All Processes I/O Bytes/sec}}\right) \times \text{System Disk Active Time \%}$$<br>• `Process I/O Bytes/sec` is derived from `IoCounters` (`ReadTransferCount + WriteTransferCount` delta) present in `SYSTEM_PROCESS_INFORMATION`.<br>• `System Disk Active Time %` is queried via PDH `\PhysicalDisk(_Total)\% Disk Time` (or `Max(% Disk Time)` across physical drives) without requiring elevation.<br>• **Defensibility**: This correctly identifies processes causing disk bottlenecks/saturation on any storage media (HDD, SATA SSD, NVMe) while naturally suppressing false alarms when overall disk activity is negligible. |

### 2. Process Identity, Ancestry, and Service Resolution Strategy

When a process exceeds a threshold, IntelliMon enriches it with local metadata:
1. **Executable Path**: Acquired via `QueryFullProcessImageName` with `PROCESS_QUERY_LIMITED_INFORMATION` (succeeds even for protected/system processes where `Process.MainModule` fails).
2. **File Metadata**: `FileVersionInfo.GetVersionInfo` extracts `FileDescription`, `ProductName`, `CompanyName`, and `FileVersion`.
3. **Digital Signature / Publisher**: Extracted via Win32 `WinVerifyTrust` and `CryptQueryObject` (or `X509Certificate2.CreateFromSignedFile`). Cached in memory by file path + hash to avoid repeated disk reads.
4. **Command Line**: Read via `NtQueryInformationProcess(ProcessCommandLineInformation)` or PEB read. Vital for generic runtimes (`python.exe script.py`, `powershell.exe -File ...`, `node.exe server.js`).
5. **Windows Service Resolution**:
   - Generic hosts like `svchost.exe` host multiple critical Windows services.
   - IntelliMon queries `EnumServicesStatusEx` (`SC_ENUM_PROCESS_INFO`) to map `PID -> List<ServiceName, DisplayName>` (e.g. PID 1420 -> `wuauserv` / Windows Update).
6. **Ancestry / Parent Tree**:
   - `InheritedFromUniqueProcessId` from process snapshots links child processes to their parents (e.g. `explorer.exe -> cmd.exe -> git.exe`).

### 3. SQLite Knowledge Architecture (`ProcessKnowledge.db`)

`ProcessKnowledge.db` is the application's **single persistent knowledge artifact**. All committed process knowledge must reside in that file. The application must not depend on persistent WAL/SHM or other companion data files.

- **Rollback Journaling (`PRAGMA journal_mode = DELETE;`)**:
  - SQLite WAL mode was evaluated and rejected because it depends on persistent companion files (`-wal` and `-shm`), which prevents the primary database file from functioning as a standalone portable artifact.
  - Rollback journaling with `DELETE` ensures that journal files exist only transiently during an active transaction and are deleted upon commit.
  - IntelliMon's database writes are infrequent (only occurring when an unknown process is newly enriched by AI) and transactions are kept very short.
  - **Portability & Copying Semantics**: When no transaction is active, `ProcessKnowledge.db` contains all committed process knowledge and can be independently copied to another IntelliMon AI installation without companion files. (An arbitrary filesystem copy during an active transaction is not expected or guaranteed to be consistent, but when no transaction is active the single `.db` file is completely self-contained.)
- **Pre-seeded Baseline**:
  - Bundled with a catalog of common Windows processes and services (`dwm.exe`, `explorer.exe`, `services.exe`, `svchost.exe:wuauserv`, `MsMpEng.exe`, `SearchIndexer.exe`, etc.) so it immediately explains standard system processes without requiring any AI calls.
- **Process Fingerprinting**:
  - Processes are indexed by a compound fingerprint:
    `[BinaryName] + [Signer/Publisher] + [ServiceName (if any)] + [NormalizedPathPattern]`.

### 4. AI-Provider Abstraction & Minimal Structured Output

- **Decoupled Architecture**: Core monitoring and knowledge layers interact exclusively through an `IProcessKnowledgeEnricher` interface.
- **Gemini Implementation**: Uses Gemini via structured JSON schema enforcement (`response_mime_type: "application/json"` with JSON schema).
- **Minimum Structured AI Schema**:
  IntelliMon AI is strictly an identification and explanatory utility, not a security or malware classification tool. The structured schema contains only the fields required to populate `ProcessKnowledge.db`:
  ```json
  {
    "type": "object",
    "properties": {
      "displayName": { "type": "string", "description": "Clean, human-readable name of the process or tool." },
      "product": { "type": "string", "description": "Name of the software product or application suite." },
      "publisher": { "type": "string", "description": "Company, organization, or author that published it." },
      "summary": { "type": "string", "description": "Concise 1-2 sentence explanation of what this process does." },
      "category": { "type": "string", "description": "Concise category, e.g. 'Operating System', 'Web Browser', 'Developer Tool', 'Background Sync', 'Game', 'Utility', 'Unknown'." },
      "expectedHighResourceReason": { "type": "string", "description": "Why this process typically consumes high CPU, Memory, or Disk." },
      "fingerprintMatch": { "type": "string", "description": "Normalized identifier or service tag matched." },
      "confidence": { "type": "number", "description": "Confidence score from 0.0 to 1.0." }
    },
    "required": ["displayName", "summary", "category", "expectedHighResourceReason", "confidence"]
  }
  ```
- **Asynchronous & Non-Blocking**:
  - Unknown busy processes are submitted to a background channel (`System.Threading.Channels.Channel<T>`).
  - Strict deduplication: In-flight or recently failed lookups are not re-queried.
  - Zero disruption to monitoring: If offline, API key missing, or request times out, monitoring displays local metadata without delay.
- **Privacy First**: Sensitive path elements (e.g., `C:\Users\JohnDoe\...`) and personal arguments in command lines are scrubbed/sanitized before transmission to the AI provider.

---

## Proposed Architecture & Structure

A clean 3-project solution avoiding enterprise bloat:

```
IntelliMon/
├── IntelliMon.sln
├── src/
│   ├── IntelliMon.Core/              # Class library: Telemetry, Metadata, Knowledge DB, AI Client
│   │   ├── Telemetry/                # NtQuerySystemInformation sampler, disk active time PDH, % calculators
│   │   ├── Metadata/                 # Win32 metadata, signatures, service mapper, command lines
│   │   ├── Knowledge/                # SQLite DB (ProcessKnowledge.db), migrations, rollback journal, seed data
│   │   ├── Ai/                       # IProcessKnowledgeEnricher, Gemini provider, minimal JSON schema
│   │   └── Config/                   # AppSettings (percentage thresholds, rotation interval)
│   │
│   └── IntelliMon.App/               # WPF Desktop Application (Tray & Floating Overlay)
│       ├── Tray/                     # NotifyIcon, tray menu, balloon/click handlers
│       ├── UI/                       # Floating/pinned overlay card, carousel animation, pin toggle
│       ├── ViewModels/               # Minimal MVVM for process display card
│       └── App.xaml / App.xaml.cs    # Single instance mutex, background loop orchestrator
│
└── tests/
    └── IntelliMon.Tests/             # Unit tests for percentage thresholds, SQLite CRUD, metadata parsers
```

### Technology Stack
1. **.NET Version**: .NET 8 LTS (`net8.0-windows`).
2. **Desktop UI**: WPF (Windows Presentation Foundation) with modern frameless styling, high-DPI awareness, and smooth carousel transitions.
3. **SQLite Provider**: `Microsoft.Data.Sqlite` (lightweight, zero ORM bloat, rollback journaling).
4. **Configuration**: Standard JSON (`System.Text.Json`) in `%APPDATA%\IntelliMon\settings.json`.

---

## Configuration & Threshold Semantics

All thresholds are user-configurable percentages (0–100%):

| Threshold | Meaning | Internal Calculation | Default |
| :--- | :--- | :--- | :--- |
| `CpuThresholdPercent` | Process CPU usage relative to total CPU capacity | `deltaProcessorTime / (deltaTime * ProcessorCount) * 100%` | `20%` |
| `MemoryThresholdPercent` | Physical Working Set relative to total physical RAM | `(WorkingSetSize / TotalPhysicalMemory) * 100%` | `10%` |
| `DiskThresholdPercent` | Process disk activity relative to overall disk utilization | `(ProcessDiskBytesPerSec / TotalDiskBytesPerSec) * DiskActiveTime%` | `15%` |

---

## Implementation Milestones

Milestones are single-purpose, testable, and independently committable:

### Milestone 1: Core Process Telemetry Engine (Console Test Harness)
- **Goal**: Accurately sample per-process CPU %, Memory Working Set %, and Disk % with sub-0.1% CPU overhead using `NtQuerySystemInformation` and unprivileged PDH disk active time.
- **Deliverables**:
  - Solution and `IntelliMon.Core` structure.
  - Native NT snapshot interop (`SYSTEM_PROCESS_INFORMATION`, `IO_COUNTERS`).
  - Total physical RAM query (`GlobalMemoryStatusEx`) and PDH disk active time query (`\PhysicalDisk(_Total)\% Disk Time`).
  - Telemetry sampling engine computing percentage deltas against wall-clock time.
  - Console test harness outputting qualifying processes exceeding percentage thresholds every 1s without crashing on protected system processes.
- **Verification**: Run CPU stress (`while($true){}`) and disk write tests; verify process percentage calculations and confirm IntelliMon's own CPU impact is negligible (<0.1%).

### Milestone 2: Process Identity, Metadata & Service Resolution
- **Goal**: For any PID, resolve full executable path, file version metadata, publisher/authenticode signature, command line, parent ancestry, and Windows Service names for `svchost.exe`.
- **Deliverables**:
  - `ProcessMetadataResolver` using `QueryFullProcessImageName` and `FileVersionInfo`.
  - Authenticode signature checker with path-level in-memory cache.
  - `ServiceResolver` using `EnumServicesStatusEx` to map service host PIDs to service names.
  - Ancestry tracker reconstructing parent-child relationships.
- **Verification**: Test against system processes (`svchost.exe`, `explorer.exe`), unsigned utilities, and command-line scripts.

### Milestone 3: SQLite Knowledge Base (`ProcessKnowledge.db`)
- **Goal**: Implement the portable single-file SQLite knowledge database using rollback journaling (`PRAGMA journal_mode = DELETE;`), pre-seeded system catalog, and fast fingerprint lookups.
- **Deliverables**:
  - SQLite schema creation, rollback journal configuration (`PRAGMA journal_mode = DELETE;`), and connection manager with short-lived transactions.
  - Fingerprint matching logic (`BinaryName` + `Signer` + `ServiceName` + `PathPattern`).
  - Pre-seeded embedded catalog of common Windows processes and services.
  - Unit tests verifying CRUD operations, fingerprint lookup, and clean journal file deletion on commit.
- **Verification**: Query known processes without AI; verify zero network calls and sub-millisecond retrieval. Verify that when no transaction is active, copying `ProcessKnowledge.db` to a separate location produces a valid, fully intact database.

### Milestone 4: AI Provider Abstraction & Minimal Gemini Integration
- **Goal**: Asynchronously enrich unknown busy processes via Gemini using the minimal structured JSON schema, write results into SQLite, and handle offline/error states seamlessly.
- **Deliverables**:
  - `IProcessKnowledgeEnricher` interface.
  - `GeminiProcessEnricher` calling Gemini with minimal structured JSON schema (`displayName`, `product`, `publisher`, `summary`, `category`, `expectedHighResourceReason`, `confidence`).
  - Background queue (`Channel<T>`) with request debouncing, deduplication, and path privacy sanitization.
  - Failure/offline graceful fallback (preserves monitoring without disruption).
- **Verification**: Test with unknown dummy executable; verify structured output validation, database write, and resilience when network is disabled.

### Milestone 5: Rotation Engine & Threshold Evaluator
- **Goal**: Maintain the active pool of qualifying busy processes, evaluate independent percentage thresholds, and rotate qualifying items every ~3 seconds.
- **Deliverables**:
  - Configurable percentage threshold evaluator (CPU %, RAM %, Disk %).
  - Rotation scheduler cycling qualifying processes every 3 seconds.
  - State machine handling the "All Quiet / Nothing unusually busy" transition.
  - Unit tests simulating multiple processes crossing and clearing percentage thresholds over time.
- **Verification**: Automated tests verifying rotation ordering, process arrival, departure, and empty-state transitions.

### Milestone 6: System Tray UI & Floating Overlay Card (WPF)
- **Goal**: Build the user-facing presentation: Tray `NotifyIcon` and floating/pinned desktop card.
- **Deliverables**:
  - WPF application shell with single-instance mutex.
  - System tray icon with context menu (Pin/Unpin, Settings, Knowledge DB, Exit).
  - Frameless floating card with modern typography, percentage culprit indicators, and 3-second carousel animation.
  - Pin/unpin toggle (pinned stays topmost, unpinned auto-dismisses).
- **Verification**: Manual UI testing on Windows 10/11 taskbar, verifying DPI scaling, pin persistence, and visual rotation.

### Milestone 7: Configuration Storage & Application Polish
- **Goal**: Connect user configuration (`settings.json`), auto-start shortcut, and final integration polish.
- **Deliverables**:
  - `settings.json` loader with hot-reload or settings dialog.
  - Startup with Windows shortcut option.
  - Complete documentation and end-to-end walkthrough.
- **Verification**: End-to-end test verifying settings persistence across restarts and standard non-elevated user execution.

---

## Verification Plan

### Automated Tests
- `IntelliMon.Tests`:
  - `ThresholdEvaluatorTests`: Tests percentage threshold crossings for CPU %, RAM %, and Disk % individually and in combinations.
  - `RotationQueueTests`: Validates 3-second cycle logic, empty queue behavior, and deduplication.
  - `SqliteKnowledgeStoreTests`: Validates single-file table creation, migrations, seeding, compound fingerprint lookups, rollback journal mode (`DELETE`), short-lived transactions, and clean journal cleanup.
  - `SanitizerTests`: Validates that user names and private folder paths are stripped before AI prompts.
  - `StructuredOutputParserTests`: Tests minimal JSON schema validation and malformed payload rejection.

### Manual Verification
- **Performance Profiling**: Use Windows Task Manager to confirm IntelliMon AI consumes < 0.1% CPU and < 40 MB Working Set during active rotation.
- **Process Stress Testing**: Run CPU stress (`while($true){}`), RAM allocation, and Disk stress (large file write/copy); verify immediate percentage threshold detection, rotation, and display.
- **Single-File Database Portability**: When no write transaction is active, copy `ProcessKnowledge.db` to another location or machine; verify the single file captures all committed records and opens seamlessly in a fresh SQLite instance without requiring companion files.
- **AI Offline Resilience**: Sever internet connection while encountering an unknown process; verify that the app does not freeze and gracefully falls back to local metadata.
