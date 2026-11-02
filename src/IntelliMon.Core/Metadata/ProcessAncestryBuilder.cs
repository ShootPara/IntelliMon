using System;
using System.Collections.Generic;
using IntelliMon.Core.Telemetry;

namespace IntelliMon.Core.Metadata;

public sealed record ProcessAncestryNode(
    int ProcessId,
    string ProcessName,
    long CreateTime
)
{
    public override string ToString() => $"{ProcessName} ({ProcessId})";
}

public static class ProcessAncestryBuilder
{
    public const int MaxAncestryDepth = 10;

    /// <summary>
    /// Reconstructs the ancestry chain (root parent down to the target process)
    /// from a coherent telemetry snapshot map.
    /// Handles PID reuse, loops, terminated parents, and bounded traversal.
    /// </summary>
    public static IReadOnlyList<ProcessAncestryNode> BuildAncestry(
        int targetProcessId,
        IReadOnlyDictionary<int, ProcessTelemetrySnapshot> snapshotMap)
    {
        if (targetProcessId <= 0 || snapshotMap == null || !snapshotMap.TryGetValue(targetProcessId, out var target))
        {
            return Array.Empty<ProcessAncestryNode>();
        }

        var chain = new List<ProcessAncestryNode>(MaxAncestryDepth);
        var visitedPids = new HashSet<int>(MaxAncestryDepth);

        var current = target;
        chain.Add(new ProcessAncestryNode(current.ProcessId, current.ProcessName, current.CreateTime));
        visitedPids.Add(current.ProcessId);

        while (chain.Count < MaxAncestryDepth)
        {
            int parentPid = current.ParentProcessId;

            // Stop at root / idle / self-parenting
            if (parentPid <= 0 || parentPid == current.ProcessId)
            {
                break;
            }

            // Cycle / loop detection
            if (!visitedPids.Add(parentPid))
            {
                break;
            }

            // Check if parent process exists in snapshot
            if (!snapshotMap.TryGetValue(parentPid, out var parent))
            {
                // Parent has terminated
                break;
            }

            // PID Reuse Protection:
            // A legitimate parent must have been created before or at the same time as its child.
            // If the process currently holding parentPid has CreateTime > child.CreateTime,
            // the original parent terminated and the OS reassigned the PID to an unrelated new process.
            if (parent.CreateTime > current.CreateTime)
            {
                break;
            }

            chain.Add(new ProcessAncestryNode(parent.ProcessId, parent.ProcessName, parent.CreateTime));
            current = parent;

            // System (PID 4) is the root of kernel/system threads
            if (current.ProcessId == 4)
            {
                break;
            }
        }

        // Reverse so chain reads from root ancestor -> ... -> target child process
        chain.Reverse();
        return chain;
    }
}
