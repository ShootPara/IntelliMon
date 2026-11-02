using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.IO;
using IntelliMon.Core.Metadata;
using Microsoft.Data.Sqlite;

namespace IntelliMon.Core.Knowledge;

public sealed class SqliteKnowledgeStore : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly ConcurrentDictionary<int, DateTime> _lastSeenUpdatedCache = new();
    private readonly TimeSpan _lastSeenDebounceInterval = TimeSpan.FromMinutes(5);
    private bool _disposed;

    public SqliteKnowledgeStore(string? dbPath = null)
    {
        _dbPath = dbPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ProcessKnowledge.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        InitializeDatabase();
    }

    public string DatabasePath => _dbPath;

    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // Enforce DELETE journal mode (no persistent WAL/SHM companion files)
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode = DELETE;";
            cmd.ExecuteNonQuery();
        }

        // Create table and indexes
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS ProcessKnowledge (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    BinaryName TEXT NOT NULL,
                    Publisher TEXT,
                    ServiceName TEXT,
                    PathPattern TEXT,
                    DisplayName TEXT NOT NULL,
                    ProductName TEXT,
                    Summary TEXT NOT NULL,
                    Category TEXT NOT NULL,
                    Source TEXT NOT NULL,
                    Confidence REAL NOT NULL,
                    FirstSeenUtc TEXT NOT NULL,
                    LastSeenUtc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_ProcessKnowledge_BinaryName ON ProcessKnowledge(BinaryName);
                CREATE INDEX IF NOT EXISTS IX_ProcessKnowledge_ServiceName ON ProcessKnowledge(ServiceName);
            ";
            cmd.ExecuteNonQuery();
        }

        // Seed initial baseline catalog if table is empty
        SeedBaselineCatalogIfEmpty(connection);
    }

    private void SeedBaselineCatalogIfEmpty(SqliteConnection connection)
    {
        using (var checkCmd = connection.CreateCommand())
        {
            checkCmd.CommandText = "SELECT COUNT(*) FROM ProcessKnowledge;";
            long count = (long)(checkCmd.ExecuteScalar() ?? 0L);
            if (count > 0)
            {
                return;
            }
        }

        var nowIso = DateTime.UtcNow.ToString("o");

        var baselineEntries = new[]
        {
            new ProcessKnowledgeEntry(0, "explorer.exe", "Microsoft Corporation", null, null, "Windows Explorer", "Microsoft® Windows® Operating System", "Windows desktop and file manager.", "Operating System", KnowledgeSource.BuiltIn, 1.0, DateTime.UtcNow, DateTime.UtcNow),
            new ProcessKnowledgeEntry(0, "dwm.exe", "Microsoft Corporation", null, null, "Desktop Window Manager", "Microsoft® Windows® Operating System", "Renders window frames and visual desktop effects.", "Operating System", KnowledgeSource.BuiltIn, 1.0, DateTime.UtcNow, DateTime.UtcNow),
            new ProcessKnowledgeEntry(0, "services.exe", "Microsoft Corporation", null, null, "Services & Controller App", "Microsoft® Windows® Operating System", "Manages background system services and drivers.", "Operating System", KnowledgeSource.BuiltIn, 1.0, DateTime.UtcNow, DateTime.UtcNow),
            new ProcessKnowledgeEntry(0, "SearchIndexer.exe", "Microsoft Corporation", null, null, "Windows Search", "Microsoft® Windows® Operating System", "Indexes files so Windows can search them quickly.", "Operating System", KnowledgeSource.BuiltIn, 1.0, DateTime.UtcNow, DateTime.UtcNow),
            new ProcessKnowledgeEntry(0, "MsMpEng.exe", "Microsoft Corporation", null, null, "Windows Defender", "Microsoft® Windows® Operating System", "Microsoft's built-in antimalware service.", "Operating System", KnowledgeSource.BuiltIn, 1.0, DateTime.UtcNow, DateTime.UtcNow),

            // svchost.exe service host entries
            new ProcessKnowledgeEntry(0, "svchost.exe", "Microsoft Corporation", "wuauserv", null, "Windows Update", "Microsoft® Windows® Operating System", "Downloads and installs operating system updates.", "Background Service", KnowledgeSource.BuiltIn, 1.0, DateTime.UtcNow, DateTime.UtcNow),
            new ProcessKnowledgeEntry(0, "svchost.exe", "Microsoft Corporation", "Dnscache", null, "DNS Client", "Microsoft® Windows® Operating System", "Caches domain name resolution queries for faster network lookup.", "Background Service", KnowledgeSource.BuiltIn, 1.0, DateTime.UtcNow, DateTime.UtcNow),
            new ProcessKnowledgeEntry(0, "svchost.exe", "Microsoft Corporation", "SysMain", null, "SysMain (SuperFetch)", "Microsoft® Windows® Operating System", "Optimizes application launch speeds and system responsiveness.", "Background Service", KnowledgeSource.BuiltIn, 1.0, DateTime.UtcNow, DateTime.UtcNow),
            new ProcessKnowledgeEntry(0, "svchost.exe", "Microsoft Corporation", "AudioSrv", null, "Windows Audio", "Microsoft® Windows® Operating System", "Manages audio playback and sound hardware for applications.", "Background Service", KnowledgeSource.BuiltIn, 1.0, DateTime.UtcNow, DateTime.UtcNow),
        };

        using var tx = connection.BeginTransaction();
        foreach (var entry in baselineEntries)
        {
            InsertEntryInternal(connection, tx, entry);
        }
        tx.Commit();
    }

    public KnowledgeMatchResult? FindKnowledge(ProcessIdentityInfo identity)
    {
        if (identity == null)
        {
            return null;
        }

        var candidates = GetCandidatesForIdentity(identity);
        var match = KnowledgeMatcher.Match(identity, candidates);

        if (match != null)
        {
            // Debounced touch of LastSeenUtc
            TouchLastSeen(match.Entry.Id, DateTime.UtcNow);
        }

        return match;
    }

    private List<ProcessKnowledgeEntry> GetCandidatesForIdentity(ProcessIdentityInfo identity)
    {
        var candidates = new List<ProcessKnowledgeEntry>();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM ProcessKnowledge WHERE BinaryName = @binName;";
        cmd.Parameters.AddWithValue("@binName", identity.ProcessName);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            candidates.Add(ReadEntry(reader));
        }

        return candidates;
    }

    public ProcessKnowledgeEntry SaveKnowledge(ProcessKnowledgeEntry entry)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var tx = connection.BeginTransaction();

        ProcessKnowledgeEntry result;
        if (entry.Id > 0)
        {
            UpdateEntryInternal(connection, tx, entry);
            result = entry;
        }
        else
        {
            int newId = InsertEntryInternal(connection, tx, entry);
            result = entry with { Id = newId };
        }

        tx.Commit();
        return result;
    }

    public void TouchLastSeen(int entryId, DateTime lastSeenUtc)
    {
        if (entryId <= 0)
        {
            return;
        }

        // Debounce: Only update DB if last update was more than _lastSeenDebounceInterval ago
        if (_lastSeenUpdatedCache.TryGetValue(entryId, out var lastUpdatedUtc))
        {
            if (lastSeenUtc - lastUpdatedUtc < _lastSeenDebounceInterval)
            {
                return;
            }
        }

        _lastSeenUpdatedCache[entryId] = lastSeenUtc;

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE ProcessKnowledge SET LastSeenUtc = @lastSeen WHERE Id = @id;";
        cmd.Parameters.AddWithValue("@lastSeen", lastSeenUtc.ToString("o"));
        cmd.Parameters.AddWithValue("@id", entryId);
        cmd.ExecuteNonQuery();
    }

    public List<ProcessKnowledgeEntry> GetAllEntries()
    {
        var list = new List<ProcessKnowledgeEntry>();
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM ProcessKnowledge ORDER BY Id ASC;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(ReadEntry(reader));
        }

        return list;
    }

    private static int InsertEntryInternal(SqliteConnection connection, SqliteTransaction tx, ProcessKnowledgeEntry entry)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO ProcessKnowledge (
                BinaryName, Publisher, ServiceName, PathPattern, DisplayName, ProductName, Summary, Category, Source, Confidence, FirstSeenUtc, LastSeenUtc
            ) VALUES (
                @binName, @publisher, @serviceName, @pathPattern, @displayName, @productName, @summary, @category, @source, @confidence, @firstSeen, @lastSeen
            );
            SELECT last_insert_rowid();
        ";

        cmd.Parameters.AddWithValue("@binName", entry.BinaryName);
        cmd.Parameters.AddWithValue("@publisher", (object?)entry.Publisher ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@serviceName", (object?)entry.ServiceName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@pathPattern", (object?)entry.PathPattern ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@displayName", entry.DisplayName);
        cmd.Parameters.AddWithValue("@productName", (object?)entry.ProductName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@summary", entry.Summary);
        cmd.Parameters.AddWithValue("@category", entry.Category);
        cmd.Parameters.AddWithValue("@source", entry.Source.ToString());
        cmd.Parameters.AddWithValue("@confidence", entry.Confidence);
        cmd.Parameters.AddWithValue("@firstSeen", entry.FirstSeenUtc.ToString("o"));
        cmd.Parameters.AddWithValue("@lastSeen", entry.LastSeenUtc.ToString("o"));

        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    private static void UpdateEntryInternal(SqliteConnection connection, SqliteTransaction tx, ProcessKnowledgeEntry entry)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            UPDATE ProcessKnowledge SET
                BinaryName = @binName,
                Publisher = @publisher,
                ServiceName = @serviceName,
                PathPattern = @pathPattern,
                DisplayName = @displayName,
                ProductName = @productName,
                Summary = @summary,
                Category = @category,
                Source = @source,
                Confidence = @confidence,
                LastSeenUtc = @lastSeen
            WHERE Id = @id;
        ";

        cmd.Parameters.AddWithValue("@id", entry.Id);
        cmd.Parameters.AddWithValue("@binName", entry.BinaryName);
        cmd.Parameters.AddWithValue("@publisher", (object?)entry.Publisher ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@serviceName", (object?)entry.ServiceName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@pathPattern", (object?)entry.PathPattern ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@displayName", entry.DisplayName);
        cmd.Parameters.AddWithValue("@productName", (object?)entry.ProductName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@summary", entry.Summary);
        cmd.Parameters.AddWithValue("@category", entry.Category);
        cmd.Parameters.AddWithValue("@source", entry.Source.ToString());
        cmd.Parameters.AddWithValue("@confidence", entry.Confidence);
        cmd.Parameters.AddWithValue("@lastSeen", entry.LastSeenUtc.ToString("o"));

        cmd.ExecuteNonQuery();
    }

    private static ProcessKnowledgeEntry ReadEntry(SqliteDataReader reader)
    {
        return new ProcessKnowledgeEntry(
            Id: reader.GetInt32(reader.GetOrdinal("Id")),
            BinaryName: reader.GetString(reader.GetOrdinal("BinaryName")),
            Publisher: reader.IsDBNull(reader.GetOrdinal("Publisher")) ? null : reader.GetString(reader.GetOrdinal("Publisher")),
            ServiceName: reader.IsDBNull(reader.GetOrdinal("ServiceName")) ? null : reader.GetString(reader.GetOrdinal("ServiceName")),
            PathPattern: reader.IsDBNull(reader.GetOrdinal("PathPattern")) ? null : reader.GetString(reader.GetOrdinal("PathPattern")),
            DisplayName: reader.GetString(reader.GetOrdinal("DisplayName")),
            ProductName: reader.IsDBNull(reader.GetOrdinal("ProductName")) ? null : reader.GetString(reader.GetOrdinal("ProductName")),
            Summary: reader.GetString(reader.GetOrdinal("Summary")),
            Category: reader.GetString(reader.GetOrdinal("Category")),
            Source: Enum.Parse<KnowledgeSource>(reader.GetString(reader.GetOrdinal("Source"))),
            Confidence: reader.GetDouble(reader.GetOrdinal("Confidence")),
            FirstSeenUtc: DateTime.Parse(reader.GetString(reader.GetOrdinal("FirstSeenUtc")), null, System.Globalization.DateTimeStyles.AdjustToUniversal),
            LastSeenUtc: DateTime.Parse(reader.GetString(reader.GetOrdinal("LastSeenUtc")), null, System.Globalization.DateTimeStyles.AdjustToUniversal)
        );
    }

    /// <summary>
    /// Verifies single-file database portability when no transactions are active.
    /// Copies ProcessKnowledge.db to targetCopyPath, opens a new connection, and verifies all records remain intact.
    /// </summary>
    public static bool VerifyDatabasePortability(string sourceDbPath, string targetCopyPath, out int recordCount)
    {
        recordCount = 0;
        if (!File.Exists(sourceDbPath))
        {
            return false;
        }

        try
        {
            // Verify no companion -wal or -shm files exist
            string directory = Path.GetDirectoryName(sourceDbPath) ?? string.Empty;
            string fileName = Path.GetFileName(sourceDbPath);
            if (File.Exists(Path.Combine(directory, fileName + "-wal")) || File.Exists(Path.Combine(directory, fileName + "-shm")))
            {
                return false;
            }

            File.Copy(sourceDbPath, targetCopyPath, overwrite: true);

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = targetCopyPath,
                Mode = SqliteOpenMode.ReadOnly
            };

            using var conn = new SqliteConnection(builder.ToString());
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM ProcessKnowledge;";
            recordCount = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);

            return recordCount > 0;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            // Microsoft.Data.Sqlite handles connection pooling; no lingering unmanaged state.
        }
    }
}
