using System;
using System.Collections.Generic;
using System.Linq;
using IntelliMon.Core.Knowledge;
using IntelliMon.Core.Metadata;
using IntelliMon.Core.Telemetry;

namespace IntelliMon.Core.Config;

/// <summary>
/// Represents a single entry in the rotation queue — a qualifying busy process
/// with its resolved identity and knowledge.
/// </summary>
public sealed class QualifyingProcessEntry
{
    public ProcessResourceSample Sample { get; init; } = null!;
    public ProcessIdentityInfo? Identity { get; set; }
    public KnowledgeMatchResult? Knowledge { get; set; }
    public DateTime FirstSeenUtc { get; init; } = DateTime.UtcNow;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Display label: knowledge DisplayName if available, else FileDescription, else ProcessName.
    /// </summary>
    public string DisplayName =>
        Knowledge?.Entry.DisplayName
        ?? Identity?.FileDescription
        ?? Sample.ProcessName;

    /// <summary>
    /// One-line summary from knowledge, or a generic placeholder.
    /// </summary>
    public string Summary =>
        Knowledge?.Entry.Summary
        ?? "Analyzing process...";

    /// <summary>
    /// Category label from knowledge, or empty.
    /// </summary>
    public string Category =>
        Knowledge?.Entry.Category ?? string.Empty;
}

/// <summary>
/// Maintains the active pool of qualifying busy processes and cycles through
/// them in a round-robin carousel.
/// </summary>
public sealed class ProcessRotationEngine
{
    private readonly object _lock = new();
    private readonly List<QualifyingProcessEntry> _active = new();
    private int _currentIndex = -1;

    public event EventHandler<RotationEventArgs>? ItemRotated;

    /// <summary>
    /// Gets the currently displayed entry, or null if the pool is empty (All Quiet).
    /// </summary>
    public QualifyingProcessEntry? Current { get; private set; }

    /// <summary>
    /// Gets a snapshot of all qualifying entries currently in the pool.
    /// </summary>
    public IReadOnlyList<QualifyingProcessEntry> ActiveEntries
    {
        get
        {
            lock (_lock) return _active.ToList();
        }
    }

    /// <summary>
    /// Updates the qualifying pool from the latest telemetry tick.
    /// Entries not seen in the latest tick are removed; new arrivals are added.
    /// </summary>
    public void UpdatePool(IEnumerable<ProcessResourceSample> qualifyingSamples)
    {
        lock (_lock)
        {
            var incomingPids = new HashSet<int>(qualifyingSamples.Select(s => s.ProcessId));

            // Remove processes that are no longer qualifying
            _active.RemoveAll(e => !incomingPids.Contains(e.Sample.ProcessId));

            // Add or update existing entries
            foreach (var sample in qualifyingSamples)
            {
                var existing = _active.FirstOrDefault(e => e.Sample.ProcessId == sample.ProcessId);
                if (existing != null)
                {
                    existing.LastSeenUtc = DateTime.UtcNow;
                }
                else
                {
                    _active.Add(new QualifyingProcessEntry
                    {
                        Sample = sample,
                        FirstSeenUtc = DateTime.UtcNow,
                        LastSeenUtc = DateTime.UtcNow
                    });
                }
            }

            // Clamp index after removal
            if (_active.Count == 0)
            {
                _currentIndex = -1;
            }
            else if (_currentIndex >= _active.Count)
            {
                _currentIndex = 0;
            }
        }
    }

    /// <summary>
    /// Enriches a specific process entry with resolved identity and knowledge data.
    /// Called after metadata/knowledge resolution completes asynchronously.
    /// </summary>
    public void SetEntryMetadata(int processId, ProcessIdentityInfo identity, KnowledgeMatchResult? knowledge)
    {
        lock (_lock)
        {
            var entry = _active.FirstOrDefault(e => e.Sample.ProcessId == processId);
            if (entry != null)
            {
                entry.Identity = identity;
                entry.Knowledge = knowledge;
            }
        }
    }

    /// <summary>
    /// Advances to the next item in the carousel.
    /// Returns the new current item (null when pool is empty = All Quiet).
    /// </summary>
    public QualifyingProcessEntry? AdvanceRotation()
    {
        lock (_lock)
        {
            if (_active.Count == 0)
            {
                Current = null;
                ItemRotated?.Invoke(this, new RotationEventArgs(null, true));
                return null;
            }

            _currentIndex = (_currentIndex + 1) % _active.Count;
            Current = _active[_currentIndex];
            ItemRotated?.Invoke(this, new RotationEventArgs(Current, false));
            return Current;
        }
    }

    /// <summary>
    /// Returns true when no processes are qualifying (All Quiet state).
    /// </summary>
    public bool IsAllQuiet
    {
        get { lock (_lock) return _active.Count == 0; }
    }
}

public sealed class RotationEventArgs : EventArgs
{
    public QualifyingProcessEntry? Entry { get; }
    public bool IsAllQuiet { get; }

    public RotationEventArgs(QualifyingProcessEntry? entry, bool isAllQuiet)
    {
        Entry = entry;
        IsAllQuiet = isAllQuiet;
    }
}
