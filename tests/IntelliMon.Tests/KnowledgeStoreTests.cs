using System;
using System.IO;
using System.Threading;
using IntelliMon.Core.Knowledge;
using IntelliMon.Core.Metadata;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IntelliMon.Tests;

public sealed class KnowledgeStoreTests : IDisposable
{
    private readonly string _tempDbPath;

    public KnowledgeStoreTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"TestKnowledge_{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        if (File.Exists(_tempDbPath))
        {
            try { File.Delete(_tempDbPath); } catch { }
        }
    }

    [Fact]
    public void SqliteKnowledgeStore_InitializesWithDeleteJournalModeAndSeedsBaseline()
    {
        using (var store = new SqliteKnowledgeStore(_tempDbPath))
        {
            var entries = store.GetAllEntries();
            Assert.NotEmpty(entries);
            Assert.True(entries.Count >= 9, "Baseline catalog should contain at least 9 entries");

            // Verify DELETE journal mode
            var builder = new SqliteConnectionStringBuilder { DataSource = _tempDbPath };
            using var conn = new SqliteConnection(builder.ToString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode;";
            string journalMode = (string)cmd.ExecuteScalar()!;
            Assert.Equal("delete", journalMode.ToLowerInvariant());
        }

        // Verify no persistent WAL/SHM companion files exist
        Assert.False(File.Exists(_tempDbPath + "-wal"));
        Assert.False(File.Exists(_tempDbPath + "-shm"));
    }

    [Fact]
    public void SqliteKnowledgeStore_MatchesKnownProcess_Explorer()
    {
        using var store = new SqliteKnowledgeStore(_tempDbPath);

        var identity = new ProcessIdentityInfo(
            ProcessId: 100,
            CreateTime: DateTime.UtcNow.Ticks,
            ProcessName: "explorer.exe",
            ExecutablePath: "C:\\Windows\\explorer.exe",
            FileDescription: "Windows Explorer",
            ProductName: "Microsoft® Windows® Operating System",
            CompanyName: "Microsoft Corporation",
            FileVersion: "10.0.26100.1",
            ProductVersion: "10.0.26100.1",
            OriginalFilename: "EXPLORER.EXE",
            SignatureStatus: SignatureVerificationStatus.SignedAndVerified,
            SignerName: "Microsoft Windows",
            CommandLine: "C:\\WINDOWS\\Explorer.EXE",
            HostedServices: Array.Empty<HostedServiceInfo>(),
            Ancestry: Array.Empty<ProcessAncestryNode>()
        );

        var result = store.FindKnowledge(identity);
        Assert.NotNull(result);
        Assert.Equal("Windows Explorer", result.Entry.DisplayName);
        Assert.Equal(KnowledgeSource.BuiltIn, result.Entry.Source);
        Assert.True(result.MatchScore >= 40);
    }

    [Fact]
    public void SqliteKnowledgeStore_MatchesServiceHostedSvchost()
    {
        using var store = new SqliteKnowledgeStore(_tempDbPath);

        var identity = new ProcessIdentityInfo(
            ProcessId: 1234,
            CreateTime: DateTime.UtcNow.Ticks,
            ProcessName: "svchost.exe",
            ExecutablePath: "C:\\Windows\\System32\\svchost.exe",
            FileDescription: "Host Process for Windows Services",
            ProductName: "Microsoft® Windows® Operating System",
            CompanyName: "Microsoft Corporation",
            FileVersion: "10.0.26100.1",
            ProductVersion: "10.0.26100.1",
            OriginalFilename: "svchost.exe",
            SignatureStatus: SignatureVerificationStatus.SignedAndVerified,
            SignerName: "Microsoft Windows",
            CommandLine: "C:\\Windows\\System32\\svchost.exe -k netsvcs -p -s wuauserv",
            HostedServices: new[] { new HostedServiceInfo("wuauserv", "Windows Update") },
            Ancestry: Array.Empty<ProcessAncestryNode>()
        );

        var result = store.FindKnowledge(identity);
        Assert.NotNull(result);
        Assert.Equal("Windows Update", result.Entry.DisplayName);
        Assert.Equal(100, result.MatchScore);
        Assert.Contains("wuauserv", result.MatchReason);
    }

    [Fact]
    public void SqliteKnowledgeStore_AvoidsUnsafeGenericRuntimeMatch_PowerShell()
    {
        using var store = new SqliteKnowledgeStore(_tempDbPath);

        var identity = new ProcessIdentityInfo(
            ProcessId: 5555,
            CreateTime: DateTime.UtcNow.Ticks,
            ProcessName: "powershell.exe",
            ExecutablePath: "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe",
            FileDescription: "Windows PowerShell",
            ProductName: "Microsoft® Windows® Operating System",
            CompanyName: "Microsoft Corporation",
            FileVersion: "10.0.26100.1",
            ProductVersion: "10.0.26100.1",
            OriginalFilename: "PowerShell.exe",
            SignatureStatus: SignatureVerificationStatus.SignedAndVerified,
            SignerName: "Microsoft Windows",
            CommandLine: "powershell.exe -File myscript.ps1",
            HostedServices: Array.Empty<HostedServiceInfo>(),
            Ancestry: Array.Empty<ProcessAncestryNode>()
        );

        // Without specific learned publisher or path pattern entry for powershell.exe,
        // bare executable name matching must be guarded against over-specific assignment.
        var result = store.FindKnowledge(identity);
        Assert.Null(result);
    }

    [Fact]
    public void SqliteKnowledgeStore_SavesAndMatchesLearnedRecord()
    {
        using var store = new SqliteKnowledgeStore(_tempDbPath);

        var learnedEntry = new ProcessKnowledgeEntry(
            Id: 0,
            BinaryName: "customtool.exe",
            Publisher: "Acme Corp",
            ServiceName: null,
            PathPattern: null,
            DisplayName: "Acme Custom Utility",
            ProductName: "Acme Tools",
            Summary: "Background data processing utility.",
            Category: "Utility",
            Source: KnowledgeSource.AiLearned,
            Confidence: 0.95,
            FirstSeenUtc: DateTime.UtcNow.AddHours(-1),
            LastSeenUtc: DateTime.UtcNow
        );

        var saved = store.SaveKnowledge(learnedEntry);
        Assert.True(saved.Id > 0);

        var identity = new ProcessIdentityInfo(
            ProcessId: 9999,
            CreateTime: DateTime.UtcNow.Ticks,
            ProcessName: "customtool.exe",
            ExecutablePath: "C:\\Program Files\\Acme\\customtool.exe",
            FileDescription: "Acme Data Processor",
            ProductName: "Acme Tools",
            CompanyName: "Acme Corp",
            FileVersion: "1.0.0",
            ProductVersion: "1.0.0",
            OriginalFilename: "customtool.exe",
            SignatureStatus: SignatureVerificationStatus.SignedAndVerified,
            SignerName: "Acme Corp",
            CommandLine: "customtool.exe --run",
            HostedServices: Array.Empty<HostedServiceInfo>(),
            Ancestry: Array.Empty<ProcessAncestryNode>()
        );

        var matched = store.FindKnowledge(identity);
        Assert.NotNull(matched);
        Assert.Equal("Acme Custom Utility", matched.Entry.DisplayName);
        Assert.Equal(KnowledgeSource.AiLearned, matched.Entry.Source);
        Assert.Equal(80, matched.MatchScore); // Publisher match score
    }

    [Fact]
    public void SqliteKnowledgeStore_PreservesFirstSeenAndUpdatesLastSeen()
    {
        using var store = new SqliteKnowledgeStore(_tempDbPath);

        var firstSeen = DateTime.UtcNow.AddDays(-5);
        var initial = new ProcessKnowledgeEntry(
            Id: 0,
            BinaryName: "myapp.exe",
            Publisher: "Test Inc",
            ServiceName: null,
            PathPattern: null,
            DisplayName: "Test Application",
            ProductName: "Test App",
            Summary: "Testing first/last seen functionality.",
            Category: "User Application",
            Source: KnowledgeSource.UserDefined,
            Confidence: 1.0,
            FirstSeenUtc: firstSeen,
            LastSeenUtc: firstSeen
        );

        var saved = store.SaveKnowledge(initial);
        Assert.Equal(firstSeen.ToString("o"), saved.FirstSeenUtc.ToString("o"));

        // Touch last seen
        var newLastSeen = DateTime.UtcNow;
        store.TouchLastSeen(saved.Id, newLastSeen);

        var all = store.GetAllEntries();
        var updated = all.Find(e => e.Id == saved.Id);
        Assert.NotNull(updated);
        Assert.Equal(saved.Id, updated.Id);
        Assert.Equal(firstSeen.ToString("s"), updated.FirstSeenUtc.ToString("s"));
        Assert.Equal(newLastSeen.ToString("s"), updated.LastSeenUtc.ToString("s"));
    }

    [Fact]
    public void SqliteKnowledgeStore_VerifiesSingleFileDatabasePortability()
    {
        using (var store = new SqliteKnowledgeStore(_tempDbPath))
        {
            // Verify DB exists and has records
            Assert.NotEmpty(store.GetAllEntries());
        }

        string copyPath = Path.Combine(Path.GetTempPath(), $"CopiedKnowledge_{Guid.NewGuid():N}.db");
        try
        {
            bool ok = SqliteKnowledgeStore.VerifyDatabasePortability(_tempDbPath, copyPath, out int recordCount);
            Assert.True(ok, "Database portability verification should succeed");
            Assert.True(recordCount >= 9, "Copied database should contain all committed records");
        }
        finally
        {
            if (File.Exists(copyPath))
            {
                try { File.Delete(copyPath); } catch { }
            }
        }
    }
}
