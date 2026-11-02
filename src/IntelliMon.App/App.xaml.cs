using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using IntelliMon.App.UI;
using IntelliMon.App.ViewModels;
using IntelliMon.Core.Ai;
using IntelliMon.Core.Config;
using IntelliMon.Core.Knowledge;
using IntelliMon.Core.Metadata;
using IntelliMon.Core.Telemetry;

namespace IntelliMon.App;

/// <summary>
/// IntelliMon AI — WPF application entry point.
/// Manages the single-instance mutex, system tray icon, overlay window, and monitoring loop.
/// </summary>
public partial class App : System.Windows.Application
{
    private const string MutexName = "IntelliMon_AI_SingleInstance_{E4B7C2A1-9F3D-4E6B-8A2C-1D5F7E9B3C4A}";

    private Mutex? _singleInstanceMutex;
    private NotifyIcon? _trayIcon;
    private OverlayWindow? _overlay;
    private CancellationTokenSource? _cts;

    // Core services
    private ProcessTelemetryEngine? _telemetryEngine;
    private ProcessMetadataResolver? _metadataResolver;
    private SqliteKnowledgeStore? _knowledgeStore;
    private ThresholdEvaluator? _thresholdEvaluator;
    private ProcessRotationEngine? _rotationEngine;
    private GeminiProcessEnricher? _enricher;
    private AppSettings _settings = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        // ── Single-instance guard ─────────────────────────────────────────────
        _singleInstanceMutex = new Mutex(true, MutexName, out bool isNew);
        if (!isNew)
        {
            System.Windows.MessageBox.Show(
                "IntelliMon AI is already running. Check the system tray.",
                "IntelliMon AI", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        _settings = SettingsLoader.Load();

        InitializeCoreServices();
        InitializeTrayIcon();
        ShowOverlay();
        StartMonitoring();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Initialization
    // ─────────────────────────────────────────────────────────────────────────

    private void InitializeCoreServices()
    {
        _telemetryEngine = new ProcessTelemetryEngine();
        _metadataResolver = new ProcessMetadataResolver();
        _knowledgeStore = new SqliteKnowledgeStore();
        _thresholdEvaluator = new ThresholdEvaluator(_settings);
        _rotationEngine = new ProcessRotationEngine();

        // AI enrichment (optional — gracefully skipped when no API key)
        if (!string.IsNullOrWhiteSpace(_settings.GeminiApiKey))
        {
            _enricher = new GeminiProcessEnricher(_settings.GeminiApiKey, _knowledgeStore);
        }
    }

    private void InitializeTrayIcon()
    {
        _trayIcon = new NotifyIcon
        {
            Icon = CreateEmbeddedIcon(),
            Text = "IntelliMon AI",
            Visible = true
        };

        var menu = new ContextMenuStrip();

        var showItem = new ToolStripMenuItem("Show Overlay");
        showItem.Click += (_, _) => ShowOverlay();
        menu.Items.Add(showItem);

        var hideItem = new ToolStripMenuItem("Hide Overlay");
        hideItem.Click += (_, _) => HideOverlay();
        menu.Items.Add(hideItem);

        menu.Items.Add(new ToolStripSeparator());

        var settingsItem = new ToolStripMenuItem("Open Settings Folder");
        settingsItem.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start("explorer.exe", AppSettings.GetSettingsDirectory()); }
            catch { }
        };
        menu.Items.Add(settingsItem);

        var dbItem = new ToolStripMenuItem("Open Knowledge DB Folder");
        dbItem.Click += (_, _) =>
        {
            if (_knowledgeStore != null)
            {
                string? dir = Path.GetDirectoryName(_knowledgeStore.DatabasePath);
                if (dir != null)
                {
                    try { System.Diagnostics.Process.Start("explorer.exe", dir); } catch { }
                }
            }
        };
        menu.Items.Add(dbItem);

        menu.Items.Add(new ToolStripSeparator());

        var startupItem = new ToolStripMenuItem("Start with Windows")
        {
            Checked = _settings.StartWithWindows,
            CheckOnClick = true
        };
        startupItem.CheckedChanged += (_, _) =>
        {
            _settings.StartWithWindows = startupItem.Checked;
            SettingsLoader.Save(_settings);
            ManageStartupShortcut(_settings.StartWithWindows);
        };
        menu.Items.Add(startupItem);

        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Exit IntelliMon AI");
        exitItem.Click += (_, _) => ExitApplication();
        menu.Items.Add(exitItem);

        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => ToggleOverlay();
    }

    private static Icon CreateEmbeddedIcon()
    {
        // Generate a simple colored icon at runtime — no external ICO file required
        using var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);

        // Deep navy background circle
        using var bgBrush = new SolidBrush(Color.FromArgb(230, 30, 40, 100));
        g.FillEllipse(bgBrush, 1, 1, 14, 14);

        // Accent ring
        using var ringPen = new System.Drawing.Pen(Color.FromArgb(200, 108, 142, 255), 1.2f);
        g.DrawEllipse(ringPen, 1, 1, 13, 13);

        // "I" letter
        using var font = new Font("Segoe UI", 8f, System.Drawing.FontStyle.Bold);
        using var textBrush = new SolidBrush(Color.White);
        g.DrawString("I", font, textBrush, new PointF(4.0f, 2.5f));

        IntPtr hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Overlay visibility
    // ─────────────────────────────────────────────────────────────────────────

    private void ShowOverlay()
    {
        Dispatcher.Invoke(() =>
        {
            if (_overlay == null || !_overlay.IsLoaded)
            {
                _overlay = new OverlayWindow();
                _overlay.ViewModel.SetAllQuiet();
            }
            _overlay.Show();
        });
    }

    private void HideOverlay()
    {
        Dispatcher.Invoke(() => _overlay?.Hide());
    }

    private void ToggleOverlay()
    {
        Dispatcher.Invoke(() =>
        {
            if (_overlay?.IsVisible == true)
                _overlay.Hide();
            else
                ShowOverlay();
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Monitoring loop
    // ─────────────────────────────────────────────────────────────────────────

    private void StartMonitoring()
    {
        _cts = new CancellationTokenSource();

        // Start AI enrichment background loop (if configured)
        if (_enricher != null)
        {
            _ = Task.Run(() => _enricher.StartAsync(_cts.Token), _cts.Token);
        }

        // Start telemetry + rotation loop
        _ = Task.Run(() => MonitoringLoopAsync(_cts.Token), _cts.Token);
    }

    private async Task MonitoringLoopAsync(CancellationToken ct)
    {
        // Warm up: take one baseline snapshot before computing deltas
        _telemetryEngine!.SampleProcesses(out _, out _);

        var rotationInterval = TimeSpan.FromSeconds(_settings.RotationIntervalSeconds);
        var sampleInterval = TimeSpan.FromSeconds(1.0);
        var nextRotation = DateTime.UtcNow + rotationInterval;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(sampleInterval, ct).ConfigureAwait(false);

                // 1. Sample telemetry
                var samples = _telemetryEngine.SampleProcesses(out _, out _);

                // 2. Find qualifying processes
                var qualifying = new System.Collections.Generic.List<Core.Telemetry.ProcessResourceSample>();
                foreach (var s in samples)
                {
                    if (_thresholdEvaluator!.IsQualifying(s))
                        qualifying.Add(s);
                }

                // 3. Update rotation pool
                _rotationEngine!.UpdatePool(qualifying);

                // 4. Resolve metadata + knowledge for newly-added entries (background)
                foreach (var entry in _rotationEngine.ActiveEntries)
                {
                    if (entry.Identity == null)
                    {
                        var e = entry; // capture
                        var snap = _telemetryEngine.GetLatestSnapshots();
                        _ = Task.Run(() =>
                        {
                            try
                            {
                                var snap2 = snap;
                                var identity = _metadataResolver!.Resolve(
                                    e.Sample.ProcessId, e.Sample.ProcessName,
                                    snap2.TryGetValue(e.Sample.ProcessId, out var s) ? s.CreateTime : 0,
                                    snap2);

                                var knowledge = _knowledgeStore!.FindKnowledge(identity);
                                _rotationEngine.SetEntryMetadata(e.Sample.ProcessId, identity, knowledge);

                                // Submit unknown processes for AI enrichment
                                if (knowledge == null && _enricher != null)
                                {
                                    _enricher.EnqueueForEnrichment(identity);
                                }
                            }
                            catch { /* Graceful degradation */ }
                        }, ct);
                    }
                }

                // 5. Rotate carousel on schedule
                if (DateTime.UtcNow >= nextRotation)
                {
                    nextRotation = DateTime.UtcNow + rotationInterval;
                    var current = _rotationEngine.AdvanceRotation();
                    UpdateOverlay(current);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Never crash the monitoring loop
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
        }
    }

    private void UpdateOverlay(Core.Config.QualifyingProcessEntry? entry)
    {
        var vm = new ProcessCardViewModel();
        if (entry == null || _rotationEngine!.IsAllQuiet)
        {
            vm.SetAllQuiet();
        }
        else
        {
            string reason = _thresholdEvaluator!.GetTriggerReason(entry.Sample);
            vm.SetEntry(entry, reason);
        }

        Dispatcher.Invoke(() => _overlay?.ShowEntry(vm));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Startup shortcut (M7)
    // ─────────────────────────────────────────────────────────────────────────

    private static void ManageStartupShortcut(bool enable)
    {
        string startupDir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        string shortcutPath = Path.Combine(startupDir, "IntelliMon AI.lnk");

        if (enable)
        {
            try
            {
                // Use Windows Script Host to create a .lnk shortcut
                string exePath = Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location;
                CreateShortcut(shortcutPath, exePath, "IntelliMon AI");
            }
            catch { }
        }
        else
        {
            try { if (File.Exists(shortcutPath)) File.Delete(shortcutPath); }
            catch { }
        }
    }

    private static void CreateShortcut(string shortcutPath, string targetPath, string description)
    {
        // Use COM interop via dynamic to avoid requiring the Interop.IWshRuntimeLibrary assembly
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return;

        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = targetPath;
        shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath) ?? string.Empty;
        shortcut.Description = description;
        shortcut.Save();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Shutdown
    // ─────────────────────────────────────────────────────────────────────────

    private void ExitApplication()
    {
        _cts?.Cancel();

        _trayIcon?.Dispose();
        _trayIcon = null;

        _enricher?.Dispose();
        _telemetryEngine?.Dispose();
        _knowledgeStore?.Dispose();

        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _cts?.Cancel();
        base.OnExit(e);
    }
}
