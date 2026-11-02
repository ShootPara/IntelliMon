using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using IntelliMon.Core.Knowledge;
using IntelliMon.Core.Metadata;
using IntelliMon.Core.Telemetry;

namespace IntelliMon.TestHarness;

public static class Program
{
    public static int Main(string[] args)
    {
        Console.WriteLine("===============================================================================");
        Console.WriteLine(" IntelliMon AI - Milestone 3 SQLite Knowledge Store Test Harness");
        Console.WriteLine(" Process Telemetry -> Resolved Metadata -> SQLite Knowledge Lookup");
        Console.WriteLine("===============================================================================");
        Console.WriteLine();

        int maxSamples = -1;
        bool demoMetadata = true;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-samples" && i + 1 < args.Length && int.TryParse(args[i + 1], out int s))
            {
                maxSamples = s;
            }
            else if (args[i] == "-nodemo")
            {
                demoMetadata = false;
            }
        }

        using var engine = new ProcessTelemetryEngine();
        using var knowledgeStore = new SqliteKnowledgeStore();
        var metadataResolver = new ProcessMetadataResolver();
        using var currentProc = Process.GetCurrentProcess();

        Console.WriteLine($"System Physical RAM: {engine.MemoryMonitor.TotalPhysicalGigabytes:F2} GB ({engine.TotalPhysicalMemory:N0} bytes)");
        Console.WriteLine($"Processor Count:     {Environment.ProcessorCount} logical cores");
        Console.WriteLine($"Knowledge DB Path:   {knowledgeStore.DatabasePath}");
        Console.WriteLine($"Self PID:            {currentProc.Id} ({currentProc.ProcessName})");
        Console.WriteLine();
        Console.WriteLine("Initializing baseline telemetry snapshot...");

        // First sample establishes delta baseline and captures initial snapshot map
        engine.SampleProcesses(out _, out _);
        var snapshotMap = engine.GetLatestSnapshots();

        if (demoMetadata)
        {
            Console.WriteLine();
            Console.WriteLine("===============================================================================");
            Console.WriteLine(" MILESTONE 3: LIVE PROCESS KNOWLEDGE LOOKUP DEMONSTRATION");
            Console.WriteLine("===============================================================================");

            // 1. Lookup explorer.exe
            LookupProcessByName(snapshotMap, metadataResolver, knowledgeStore, "explorer.exe", "Windows Explorer");

            // 2. Lookup svchost.exe (service-hosted matching)
            LookupProcessByName(snapshotMap, metadataResolver, knowledgeStore, "svchost.exe", "Service Host");

            // 3. Lookup third-party signed app
            bool inspectedSignedApp = LookupProcessByName(snapshotMap, metadataResolver, knowledgeStore, "ChatGPT.exe", "Signed 3rd-Party App (ChatGPT)")
                || LookupProcessByName(snapshotMap, metadataResolver, knowledgeStore, "Discord.exe", "Signed 3rd-Party App (Discord)")
                || LookupProcessByName(snapshotMap, metadataResolver, knowledgeStore, "Antigravity.exe", "Signed 3rd-Party App (Antigravity)")
                || LookupProcessByName(snapshotMap, metadataResolver, knowledgeStore, "GoogleDriveFS.exe", "Signed 3rd-Party App (Google Drive)");

            // 4. Lookup PowerShell (unseeded generic runtime -> no match)
            LookupProcessByName(snapshotMap, metadataResolver, knowledgeStore, "powershell.exe", "Generic Runtime (PowerShell)");

            // 5. Portability Verification Demonstration
            string testCopyPath = Path.Combine(Path.GetTempPath(), $"PortabilityTest_{Guid.NewGuid():N}.db");
            try
            {
                bool portOk = SqliteKnowledgeStore.VerifyDatabasePortability(knowledgeStore.DatabasePath, testCopyPath, out int recordCount);
                Console.WriteLine($"--- Database Portability Verification ---");
                Console.WriteLine($"  Copied file to: {testCopyPath}");
                Console.WriteLine($"  Status:         {(portOk ? "PASSED (Single-File Portability Verified)" : "FAILED")}");
                Console.WriteLine($"  Records Read:   {recordCount}");
                Console.WriteLine();
            }
            finally
            {
                if (File.Exists(testCopyPath))
                {
                    try { File.Delete(testCopyPath); } catch { }
                }
            }

            Console.WriteLine("===============================================================================");
            Console.WriteLine();
        }

        Thread.Sleep(1000);

        var cancellationTokenSource = new CancellationTokenSource();
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            cancellationTokenSource.Cancel();
        };

        TimeSpan prevSelfCpu = currentProc.TotalProcessorTime;
        DateTime prevSelfTime = DateTime.UtcNow;

        int iteration = 0;
        double totalSelfCpuAccumulated = 0.0;
        int selfCpuSampleCount = 0;

        while (!cancellationTokenSource.IsCancellationRequested)
        {
            iteration++;
            var sampleStart = Stopwatch.StartNew();

            var samples = engine.SampleProcesses(out double diskActiveTimePercent, out double totalDiskBytesPerSec);
            sampleStart.Stop();

            var latestSnapshots = engine.GetLatestSnapshots();

            // Self telemetry measurement
            currentProc.Refresh();
            TimeSpan currSelfCpu = currentProc.TotalProcessorTime;
            DateTime currSelfTime = DateTime.UtcNow;
            double selfDeltaTimeSec = (currSelfTime - prevSelfTime).TotalSeconds;
            double selfCpuPercent = 0.0;
            if (selfDeltaTimeSec > 0.001)
            {
                double selfCpuDeltaTicks = (currSelfCpu - prevSelfCpu).TotalMilliseconds * 10_000.0;
                double selfCapacityTicks = selfDeltaTimeSec * Environment.ProcessorCount * 10_000_000.0;
                selfCpuPercent = Math.Clamp((selfCpuDeltaTicks / selfCapacityTicks) * 100.0, 0.0, 100.0);
            }
            prevSelfCpu = currSelfCpu;
            prevSelfTime = currSelfTime;

            if (iteration > 1)
            {
                totalSelfCpuAccumulated += selfCpuPercent;
                selfCpuSampleCount++;
            }

            double selfWorkingSetMb = currentProc.WorkingSet64 / (1024.0 * 1024.0);
            double avgSelfCpu = selfCpuSampleCount > 0 ? (totalSelfCpuAccumulated / selfCpuSampleCount) : selfCpuPercent;

            Console.WriteLine($"-------------------------------------------------------------------------------");
            Console.WriteLine($"[Sample #{iteration:D3} @ {DateTime.Now:HH:mm:ss.fff}] Snapshots: {samples.Count} processes (sampling took {sampleStart.Elapsed.TotalMilliseconds:F2} ms)");
            Console.WriteLine($"Disk Active: {diskActiveTimePercent,5:F1}% | Total Disk I/O: {totalDiskBytesPerSec / (1024.0 * 1024.0),6:F2} MB/s");
            Console.WriteLine($"Self Overhead: CPU = {selfCpuPercent,4:F2}% (Avg: {avgSelfCpu,4:F2}%) | Working Set = {selfWorkingSetMb,5:F1} MB | DB Entries: {knowledgeStore.GetAllEntries().Count}");
            Console.WriteLine($"-------------------------------------------------------------------------------");

            // Top 3 by CPU
            var topCpu = samples.Where(s => s.ProcessId != 0)
                                .OrderByDescending(s => s.CpuPercent)
                                .Take(3)
                                .ToList();

            Console.WriteLine(" TOP CPU (excluding Idle):");
            foreach (var p in topCpu)
            {
                Console.WriteLine($"   {p}");
            }

            // Top 3 by Memory
            var topMemory = samples.OrderByDescending(s => s.MemoryWorkingSetPercent)
                                   .Take(3)
                                   .ToList();

            Console.WriteLine(" TOP MEMORY (Physical Working Set %):");
            foreach (var p in topMemory)
            {
                Console.WriteLine($"   {p}");
            }

            // Top 3 by Disk
            var topDisk = samples.OrderByDescending(s => s.DiskPercent)
                                 .Take(3)
                                 .ToList();

            Console.WriteLine(" TOP DISK (Attributed Active Time %):");
            foreach (var p in topDisk)
            {
                Console.WriteLine($"   {p}");
            }

            // Threshold Check & Local Knowledge Lookup for Qualifying Processes
            var qualifying = samples.Where(s => s.ProcessId != 0 && (s.CpuPercent >= 10.0 || s.MemoryWorkingSetPercent >= 5.0 || s.DiskPercent >= 5.0))
                                    .ToList();

            if (qualifying.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($" QUALIFYING BUSY PROCESSES (Thresholds: CPU>=10%, RAM>=5%, Disk>=5%):");
                foreach (var q in qualifying)
                {
                    Console.WriteLine($"   * {q}");
                    if (latestSnapshots.TryGetValue(q.ProcessId, out var snap))
                    {
                        var identity = metadataResolver.Resolve(q.ProcessId, q.ProcessName, snap.CreateTime, latestSnapshots);
                        var match = knowledgeStore.FindKnowledge(identity);

                        if (match != null)
                        {
                            Console.WriteLine($"     KNOWLEDGE: \"{match.Entry.DisplayName}\" ({match.Entry.Category})");
                            Console.WriteLine($"     EXPLANATION: {match.Entry.Summary}");
                            Console.WriteLine($"     MATCH INFO:  Score {match.MatchScore} via {match.MatchReason} [Source: {match.Entry.Source}]");
                        }
                        else
                        {
                            Console.WriteLine($"     KNOWLEDGE: <No local knowledge match - Eligible for AI enrichment>");
                            Console.WriteLine($"     IDENTITY:  {identity.PrimaryDescription} | Publisher: {identity.SignerName ?? identity.CompanyName ?? "<Unverified>"}");
                        }
                    }
                }
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.DarkGreen;
                Console.WriteLine(" STATUS: All Quiet (No process exceeds resource thresholds)");
                Console.ResetColor();
            }

            Console.WriteLine();

            if (maxSamples > 0 && iteration >= maxSamples)
            {
                break;
            }

            Thread.Sleep(1000);
        }

        Console.WriteLine("===============================================================================");
        Console.WriteLine($" Test run completed. Iterations: {iteration}. Self Average CPU: {(selfCpuSampleCount > 0 ? (totalSelfCpuAccumulated / selfCpuSampleCount) : 0.0):F2}%");
        Console.WriteLine("===============================================================================");

        return 0;
    }

    private static bool LookupProcessByName(
        System.Collections.Generic.IReadOnlyDictionary<int, ProcessTelemetrySnapshot> snapshotMap,
        ProcessMetadataResolver resolver,
        SqliteKnowledgeStore knowledgeStore,
        string targetName,
        string label)
    {
        var match = snapshotMap.Values.FirstOrDefault(s => s.ProcessName.Equals(targetName, StringComparison.OrdinalIgnoreCase));
        if (match.ProcessId == 0)
        {
            return false;
        }

        var identity = resolver.Resolve(match.ProcessId, match.ProcessName, match.CreateTime, snapshotMap);
        var knowledgeMatch = knowledgeStore.FindKnowledge(identity);

        Console.WriteLine($"--- Target: {label} (PID {identity.ProcessId}) ---");
        Console.WriteLine($"  Executable Path: {identity.ExecutablePath ?? "<Inaccessible>"}");
        Console.WriteLine($"  Primary Info:    {identity.PrimaryDescription}");

        if (identity.HostedServices.Count > 0)
        {
            Console.WriteLine($"  Hosted Svcs:     {string.Join(", ", identity.HostedServices.Select(s => $"{s.ServiceName} ({s.DisplayName})"))}");
        }

        if (knowledgeMatch != null)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  [KNOWLEDGE MATCH FOUND]");
            Console.WriteLine($"  Display Name:    {knowledgeMatch.Entry.DisplayName}");
            Console.WriteLine($"  Category:        {knowledgeMatch.Entry.Category}");
            Console.WriteLine($"  Explanation:     {knowledgeMatch.Entry.Summary}");
            Console.WriteLine($"  Match Details:   Score {knowledgeMatch.MatchScore} via {knowledgeMatch.MatchReason} (Source: {knowledgeMatch.Entry.Source})");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  [NO LOCAL KNOWLEDGE MATCH]");
            Console.WriteLine($"  Status:          Eligible for asynchronous AI enrichment");
            Console.ResetColor();
        }

        Console.WriteLine();
        return true;
    }
}
