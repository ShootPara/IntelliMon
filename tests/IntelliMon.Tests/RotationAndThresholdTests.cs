using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IntelliMon.Core.Ai;
using IntelliMon.Core.Config;
using IntelliMon.Core.Knowledge;
using IntelliMon.Core.Metadata;
using IntelliMon.Core.Telemetry;
using Xunit;

namespace IntelliMon.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// ThresholdEvaluatorTests  (Milestone 5)
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ThresholdEvaluatorTests
{
    private static AppSettings DefaultSettings() => new AppSettings
    {
        CpuThresholdPercent = 20.0,
        MemoryThresholdPercent = 10.0,
        DiskThresholdPercent = 15.0
    };

    private static ProcessResourceSample MakeSample(int pid, double cpu = 0, double ram = 0, double disk = 0)
        => new(pid, 0, $"proc_{pid}.exe", cpu, ram, 0, 0, disk);

    [Fact]
    public void ThresholdEvaluator_FlagsHighCpuProcess()
    {
        var ev = new ThresholdEvaluator(DefaultSettings());
        Assert.True(ev.IsQualifying(MakeSample(100, cpu: 25.0)));
    }

    [Fact]
    public void ThresholdEvaluator_FlagsHighRamProcess()
    {
        var ev = new ThresholdEvaluator(DefaultSettings());
        Assert.True(ev.IsQualifying(MakeSample(100, ram: 12.0)));
    }

    [Fact]
    public void ThresholdEvaluator_FlagsHighDiskProcess()
    {
        var ev = new ThresholdEvaluator(DefaultSettings());
        Assert.True(ev.IsQualifying(MakeSample(100, disk: 20.0)));
    }

    [Fact]
    public void ThresholdEvaluator_DoesNotFlagBelowThreshold()
    {
        var ev = new ThresholdEvaluator(DefaultSettings());
        Assert.False(ev.IsQualifying(MakeSample(100, cpu: 19.9, ram: 9.9, disk: 14.9)));
    }

    [Fact]
    public void ThresholdEvaluator_ExcludesSystemIdleAndSystemProcess()
    {
        var ev = new ThresholdEvaluator(DefaultSettings());
        Assert.False(ev.IsQualifying(MakeSample(0, cpu: 99.0)));  // System Idle
        Assert.False(ev.IsQualifying(MakeSample(4, cpu: 50.0)));  // System
    }

    [Fact]
    public void ThresholdEvaluator_ExactlyAtThresholdIsQualifying()
    {
        var ev = new ThresholdEvaluator(DefaultSettings());
        Assert.True(ev.IsQualifying(MakeSample(100, cpu: 20.0)));
        Assert.True(ev.IsQualifying(MakeSample(200, ram: 10.0)));
        Assert.True(ev.IsQualifying(MakeSample(300, disk: 15.0)));
    }

    [Fact]
    public void ThresholdEvaluator_GetTriggerReason_ListsAllExceededDimensions()
    {
        var ev = new ThresholdEvaluator(DefaultSettings());
        string reason = ev.GetTriggerReason(MakeSample(100, cpu: 25.0, ram: 11.0, disk: 20.0));
        Assert.Contains("CPU", reason);
        Assert.Contains("RAM", reason);
        Assert.Contains("Disk", reason);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ProcessRotationEngineTests  (Milestone 5)
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ProcessRotationEngineTests
{
    private static ProcessResourceSample MakeSample(int pid)
        => new(pid, 0, $"proc_{pid}.exe", 25.0, 0, 0, 0, 0);

    [Fact]
    public void RotationEngine_StartsAllQuiet_WithEmptyPool()
    {
        var engine = new ProcessRotationEngine();
        Assert.True(engine.IsAllQuiet);
        Assert.Null(engine.Current);
    }

    [Fact]
    public void RotationEngine_AddAndRotateSingleEntry()
    {
        var engine = new ProcessRotationEngine();
        engine.UpdatePool(new[] { MakeSample(100) });
        Assert.False(engine.IsAllQuiet);

        var current = engine.AdvanceRotation();
        Assert.NotNull(current);
        Assert.Equal(100, current!.Sample.ProcessId);
    }

    [Fact]
    public void RotationEngine_CyclesRoundRobin()
    {
        var engine = new ProcessRotationEngine();
        engine.UpdatePool(new[] { MakeSample(1), MakeSample(2), MakeSample(3) });

        var seen = new List<int>();
        for (int i = 0; i < 6; i++)
        {
            seen.Add(engine.AdvanceRotation()!.Sample.ProcessId);
        }

        // Should cycle: 1,2,3,1,2,3
        Assert.Equal(new[] { 1, 2, 3, 1, 2, 3 }, seen);
    }

    [Fact]
    public void RotationEngine_ReturnsNullAndFiresAllQuietWhenPoolEmpty()
    {
        var engine = new ProcessRotationEngine();
        engine.UpdatePool(new[] { MakeSample(100) });
        engine.AdvanceRotation();

        bool allQuietFired = false;
        engine.ItemRotated += (_, e) => { if (e.IsAllQuiet) allQuietFired = true; };

        engine.UpdatePool(Array.Empty<ProcessResourceSample>());
        var result = engine.AdvanceRotation();

        Assert.Null(result);
        Assert.True(allQuietFired);
        Assert.True(engine.IsAllQuiet);
    }

    [Fact]
    public void RotationEngine_RemovesExpiredEntriesOnUpdate()
    {
        var engine = new ProcessRotationEngine();
        engine.UpdatePool(new[] { MakeSample(1), MakeSample(2) });
        engine.AdvanceRotation();

        // Update pool — PID 2 is gone
        engine.UpdatePool(new[] { MakeSample(1) });
        Assert.Single(engine.ActiveEntries);
        Assert.Equal(1, engine.ActiveEntries[0].Sample.ProcessId);
    }

    [Fact]
    public void RotationEngine_SetEntryMetadata_UpdatesCorrectEntry()
    {
        var engine = new ProcessRotationEngine();
        engine.UpdatePool(new[] { MakeSample(42) });

        var identity = new ProcessIdentityInfo(
            ProcessId: 42, CreateTime: 0, ProcessName: "proc_42.exe",
            ExecutablePath: null, FileDescription: "My Process", ProductName: null,
            CompanyName: null, FileVersion: null, ProductVersion: null,
            OriginalFilename: null, SignatureStatus: SignatureVerificationStatus.Unsigned,
            SignerName: null, CommandLine: null,
            HostedServices: Array.Empty<HostedServiceInfo>(),
            Ancestry: Array.Empty<ProcessAncestryNode>()
        );

        engine.SetEntryMetadata(42, identity, null);

        var entry = engine.ActiveEntries.FirstOrDefault(e => e.Sample.ProcessId == 42);
        Assert.NotNull(entry);
        Assert.Equal("My Process", entry!.Identity!.FileDescription);
        Assert.Equal("My Process", entry.DisplayName);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// CommandLineSanitizerTests  (Milestone 4)
// ─────────────────────────────────────────────────────────────────────────────

public sealed class CommandLineSanitizerTests
{
    [Fact]
    public void Sanitizer_ReplacesUserProfilePath()
    {
        string result = CommandLineSanitizer.SanitizePath(@"C:\Users\johndoe\AppData\Local\MyApp\app.exe");
        Assert.DoesNotContain("johndoe", result);
        Assert.Contains(@"C:\Users\<user>", result);
    }

    [Fact]
    public void Sanitizer_HandlesNullOrEmptyPath()
    {
        Assert.Equal("<path unavailable>", CommandLineSanitizer.SanitizePath(null));
        Assert.Equal("<path unavailable>", CommandLineSanitizer.SanitizePath(""));
        Assert.Equal("<path unavailable>", CommandLineSanitizer.SanitizePath("   "));
    }

    [Fact]
    public void Sanitizer_PreservesNonSensitivePaths()
    {
        string result = CommandLineSanitizer.SanitizePath(@"C:\Program Files\MyApp\myapp.exe");
        Assert.Equal(@"C:\Program Files\MyApp\myapp.exe", result);
    }

    [Fact]
    public void Sanitizer_RedactsSecretArguments()
    {
        string result = CommandLineSanitizer.SanitizeCommandLine("myapp.exe --password=SuperSecret123 --output=file.txt");
        Assert.DoesNotContain("SuperSecret123", result);
        Assert.Contains("<redacted>", result);
        Assert.Contains("--output=file.txt", result);
    }

    [Fact]
    public void Sanitizer_HandlesNullCommandLine()
    {
        string result = CommandLineSanitizer.SanitizeCommandLine(null);
        Assert.Equal("<command line unavailable>", result);
    }

    [Fact]
    public void Sanitizer_ReplacesUserProfileInCommandLine()
    {
        string result = CommandLineSanitizer.SanitizeCommandLine(
            @"python.exe C:\Users\alice\scripts\train_model.py --epochs=10");
        Assert.DoesNotContain("alice", result);
        Assert.Contains("python.exe", result);
        Assert.Contains(@"C:\Users\<user>", result);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// SettingsLoaderTests  (Milestone 7)
// ─────────────────────────────────────────────────────────────────────────────

public sealed class SettingsLoaderTests
{
    [Fact]
    public void SettingsLoader_LoadsDefaultsWhenNoFileExists()
    {
        var settings = new AppSettings();
        Assert.Equal(20.0, settings.CpuThresholdPercent);
        Assert.Equal(10.0, settings.MemoryThresholdPercent);
        Assert.Equal(15.0, settings.DiskThresholdPercent);
        Assert.Equal(3.0, settings.RotationIntervalSeconds);
    }

    [Fact]
    public void SettingsLoader_RoundTripsSettings()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"IntelliMonTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string path = Path.Combine(tempDir, "settings.json");

        try
        {
            var original = new AppSettings
            {
                CpuThresholdPercent = 35.0,
                MemoryThresholdPercent = 7.5,
                DiskThresholdPercent = 12.0,
                RotationIntervalSeconds = 5.0,
                GeminiApiKey = "test-key-abc",
                StartWithWindows = true
            };

            string json = System.Text.Json.JsonSerializer.Serialize(
                original,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            json = File.ReadAllText(path);
            var loaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
                json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

            Assert.Equal(35.0, loaded.CpuThresholdPercent);
            Assert.Equal(7.5, loaded.MemoryThresholdPercent);
            Assert.Equal(12.0, loaded.DiskThresholdPercent);
            Assert.Equal(5.0, loaded.RotationIntervalSeconds);
            Assert.Equal("test-key-abc", loaded.GeminiApiKey);
            Assert.True(loaded.StartWithWindows);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
