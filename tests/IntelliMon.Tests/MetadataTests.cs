using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using IntelliMon.Core.Metadata;
using IntelliMon.Core.Telemetry;
using Xunit;

namespace IntelliMon.Tests;

public sealed class MetadataTests
{
    [Fact]
    public void ProcessAncestryBuilder_BuildsAncestryAndDetectsLoopsAndPidReuse()
    {
        long nowTicks = DateTime.UtcNow.Ticks;

        var snapshots = new Dictionary<int, ProcessTelemetrySnapshot>
        {
            [100] = new ProcessTelemetrySnapshot(100, 0, "system.exe", nowTicks - 1000, 0, 0, 0, 0, 0, 0, DateTime.UtcNow),
            [200] = new ProcessTelemetrySnapshot(200, 100, "parent.exe", nowTicks - 800, 0, 0, 0, 0, 0, 0, DateTime.UtcNow),
            [300] = new ProcessTelemetrySnapshot(300, 200, "child.exe", nowTicks - 500, 0, 0, 0, 0, 0, 0, DateTime.UtcNow),
        };

        var ancestry = ProcessAncestryBuilder.BuildAncestry(300, snapshots);
        Assert.Equal(3, ancestry.Count);
        Assert.Equal("system.exe", ancestry[0].ProcessName);
        Assert.Equal("parent.exe", ancestry[1].ProcessName);
        Assert.Equal("child.exe", ancestry[2].ProcessName);

        // Test PID Reuse: Parent created AFTER child
        var reusedSnapshots = new Dictionary<int, ProcessTelemetrySnapshot>
        {
            [200] = new ProcessTelemetrySnapshot(200, 100, "reused_parent.exe", nowTicks + 500, 0, 0, 0, 0, 0, 0, DateTime.UtcNow),
            [300] = new ProcessTelemetrySnapshot(300, 200, "child.exe", nowTicks - 500, 0, 0, 0, 0, 0, 0, DateTime.UtcNow),
        };

        var reusedAncestry = ProcessAncestryBuilder.BuildAncestry(300, reusedSnapshots);
        Assert.Single(reusedAncestry);
        Assert.Equal("child.exe", reusedAncestry[0].ProcessName);

        // Test Loop Detection: 200 -> 300 -> 200
        var loopSnapshots = new Dictionary<int, ProcessTelemetrySnapshot>
        {
            [200] = new ProcessTelemetrySnapshot(200, 300, "loop1.exe", nowTicks - 800, 0, 0, 0, 0, 0, 0, DateTime.UtcNow),
            [300] = new ProcessTelemetrySnapshot(300, 200, "loop2.exe", nowTicks - 500, 0, 0, 0, 0, 0, 0, DateTime.UtcNow),
        };

        var loopAncestry = ProcessAncestryBuilder.BuildAncestry(300, loopSnapshots);
        Assert.True(loopAncestry.Count <= 2, "Loop traversal must terminate");
    }

    [Fact]
    public void FileVersionMetadataProvider_HandlesMissingOrNonexistentFiles()
    {
        Assert.Null(FileVersionMetadataProvider.GetMetadata(null));
        Assert.Null(FileVersionMetadataProvider.GetMetadata(""));
        Assert.Null(FileVersionMetadataProvider.GetMetadata("C:\\NonExistentPath_12345.exe"));
    }

    [Fact]
    public void AuthenticodeVerifier_CachesResultsCorrectly()
    {
        var verifier = new AuthenticodeVerifier();
        string systemExePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

        if (File.Exists(systemExePath))
        {
            var result1 = verifier.Verify(systemExePath);
            Assert.NotNull(result1);
            Assert.Equal(1, verifier.CacheCount);

            var result2 = verifier.Verify(systemExePath);
            Assert.Equal(result1, result2);
            Assert.Equal(1, verifier.CacheCount);
        }

        var missingResult = verifier.Verify("C:\\NonExistentFile.exe");
        Assert.Equal(SignatureVerificationStatus.Inaccessible, missingResult.Status);
    }

    [Fact]
    public void ServiceResolver_EnumeratesServicesWithoutError()
    {
        var resolver = new ServiceResolver();
        resolver.Refresh();
        // Just verify it doesn't throw and completes
    }

    [Fact]
    public void ProcessMetadataResolver_ResolvesCurrentProcessIdentity()
    {
        using var currentProc = Process.GetCurrentProcess();
        var resolver = new ProcessMetadataResolver();

        var identity = resolver.Resolve(currentProc.Id, currentProc.ProcessName, currentProc.StartTime.Ticks);
        Assert.NotNull(identity);
        Assert.Equal(currentProc.Id, identity.ProcessId);
        Assert.Equal(currentProc.ProcessName, identity.ProcessName);
        Assert.NotNull(identity.ExecutablePath);
        Assert.True(File.Exists(identity.ExecutablePath));
    }
}
