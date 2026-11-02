# IntelliMon AI

IntelliMon AI is a lightweight native Windows desktop and system-tray utility that monitors running processes for elevated resource consumption (**CPU, Memory, and Disk**), identifies qualifying processes using rich local metadata and an offline-first SQLite knowledge base, and asynchronously enriches unknown processes via Google's Gemini API with structured JSON output.

Operates entirely as a standard, unprivileged Windows desktop utility without requiring Administrator elevation.

---

## ✨ Features

- **Negligible Overhead Telemetry**:
  - Sub-millisecond snapshot collection (<0.1% CPU) using `NtQuerySystemInformation` P/Invoke.
  - Physical RAM working set percentage calculated against total hardware RAM (`GlobalMemoryStatusEx`).
  - Proportional disk active time utilization calculated via unprivileged PDH counters (`\PhysicalDisk(_Total)\% Disk Time`).
- **Comprehensive Process Identity**:
  - Full path resolution via `QueryFullProcessImageName` (works even on protected processes).
  - Authenticode digital signature and publisher verification with caching.
  - Windows service resolution for `svchost.exe` instances via `EnumServicesStatusEx`.
  - Process ancestry tree reconstruction.
- **Offline-First Knowledge Base**:
  - Single-file portable SQLite database (`ProcessKnowledge.db`) using rollback journaling (`PRAGMA journal_mode = DELETE;`). No lingering `-wal` or `-shm` companion files.
  - Pre-seeded baseline catalog of standard Windows processes and core services.
- **Asynchronous AI Enrichment**:
  - Unknown qualifying processes are queued through a non-blocking background channel (`Channel<T>`).
  - Powered by Gemini (`gemini-3.5-flash-lite`) with structured JSON output schemas.
  - Automatic privacy scrubbing: paths and command lines sanitize user profiles and secret credentials before transmission.
- **Modern WPF Overlay & System Tray**:
  - Frameless floating card near the taskbar with smooth fade animations.
  - Real-time CPU, RAM, and Disk metric pills.
  - Pin/Unpin support (`Topmost`), draggable, and tray icon context menu.

---

## 🛠 Project Structure

```
IntelliMon/
├── IntelliMon.sln
├── Set-GeminiApiKey.ps1           # Configuration helper script
├── src/
│   ├── IntelliMon.Core/           # Core library (Telemetry, Metadata, SQLite Knowledge DB, AI)
│   ├── IntelliMon.App/            # WPF desktop application (Tray & Floating Overlay)
│   └── IntelliMon.TestHarness/    # Console test runner for telemetry verification
└── tests/
    └── IntelliMon.Tests/          # Unit tests (xUnit)
```

---

## 🚀 Getting Started

### Prerequisites
- Windows 10/11 (x64)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### Building
```powershell
dotnet build IntelliMon.sln -c Release
```

### Running Tests
```powershell
dotnet test tests/IntelliMon.Tests/IntelliMon.Tests.csproj
```

### Setup Gemini API Key
To enable background AI enrichment for unknown processes:
```powershell
.\Set-GeminiApiKey.ps1
```
Or specify it directly:
```powershell
.\Set-GeminiApiKey.ps1 -ApiKey "YOUR_GEMINI_API_KEY"
```
*(The key is saved to `%APPDATA%\IntelliMon\settings.json`.)*

### Running the App
Run the WPF executable from `src/IntelliMon.App/bin/Release/net8.0-windows/IntelliMon.exe`.
IntelliMon will start in your system tray and display the resource overlay card when processes exceed configured thresholds.

---

## ⚙️ Configuration

Settings are stored in `%APPDATA%\IntelliMon\settings.json`:

```json
{
  "cpuThresholdPercent": 20.0,
  "memoryThresholdPercent": 10.0,
  "diskThresholdPercent": 15.0,
  "rotationIntervalSeconds": 3.0,
  "startWithWindows": false,
  "geminiApiKey": "YOUR_KEY"
}
```

---

## 📄 License
MIT License
