using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using IntelliMon.Core.Config;

namespace IntelliMon.App.ViewModels;

/// <summary>
/// ViewModel for the floating overlay card. Provides all display-relevant process data
/// and raises PropertyChanged to drive WPF bindings.
/// </summary>
public sealed class ProcessCardViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private string _processName = string.Empty;
    private string _displayName = string.Empty;
    private string _category = string.Empty;
    private string _summary = string.Empty;
    private string _triggerReason = string.Empty;
    private double _cpuPercent;
    private double _memoryPercent;
    private double _diskPercent;
    private bool _isAllQuiet = true;
    private bool _hasKnowledge;
    private string _publisher = string.Empty;

    public string ProcessName
    {
        get => _processName;
        set => Set(ref _processName, value);
    }

    public string DisplayName
    {
        get => _displayName;
        set => Set(ref _displayName, value);
    }

    public string Category
    {
        get => _category;
        set => Set(ref _category, value);
    }

    public string Summary
    {
        get => _summary;
        set => Set(ref _summary, value);
    }

    public string TriggerReason
    {
        get => _triggerReason;
        set => Set(ref _triggerReason, value);
    }

    public double CpuPercent
    {
        get => _cpuPercent;
        set => Set(ref _cpuPercent, value);
    }

    public double MemoryPercent
    {
        get => _memoryPercent;
        set => Set(ref _memoryPercent, value);
    }

    public double DiskPercent
    {
        get => _diskPercent;
        set => Set(ref _diskPercent, value);
    }

    public bool IsAllQuiet
    {
        get => _isAllQuiet;
        set => Set(ref _isAllQuiet, value);
    }

    public bool HasKnowledge
    {
        get => _hasKnowledge;
        set => Set(ref _hasKnowledge, value);
    }

    public string Publisher
    {
        get => _publisher;
        set => Set(ref _publisher, value);
    }

    /// <summary>
    /// Resets the card to the "All Quiet" idle state.
    /// </summary>
    public void SetAllQuiet()
    {
        IsAllQuiet = true;
        ProcessName = string.Empty;
        DisplayName = "All Quiet";
        Category = string.Empty;
        Summary = "No unusual resource activity detected.";
        TriggerReason = string.Empty;
        CpuPercent = 0;
        MemoryPercent = 0;
        DiskPercent = 0;
        HasKnowledge = false;
        Publisher = string.Empty;
    }

    /// <summary>
    /// Updates the card with data from a qualifying rotation entry.
    /// </summary>
    public void SetEntry(QualifyingProcessEntry entry, string triggerReason)
    {
        IsAllQuiet = false;
        ProcessName = entry.Sample.ProcessName;
        DisplayName = entry.DisplayName;
        Category = entry.Category;
        Summary = entry.Summary;
        TriggerReason = triggerReason;
        CpuPercent = entry.Sample.CpuPercent;
        MemoryPercent = entry.Sample.MemoryWorkingSetPercent;
        DiskPercent = entry.Sample.DiskPercent;
        HasKnowledge = entry.Knowledge != null;
        Publisher = entry.Identity?.SignerName ?? entry.Identity?.CompanyName ?? string.Empty;
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (!Equals(field, value))
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }
}
