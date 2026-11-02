using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using IntelliMon.App.ViewModels;

// Explicit WPF type aliases to prevent ambiguity when UseWindowsForms is true
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonState = System.Windows.Input.MouseButtonState;

namespace IntelliMon.App.UI;

/// <summary>
/// Frameless floating overlay card.
/// Displays the currently active qualifying process with smooth fade-in/out transitions.
/// Supports drag-to-reposition and a pin/unpin toggle.
/// </summary>
public partial class OverlayWindow : Window
{
    private bool _isPinned = false;
    private bool _isFading = false;
    private System.Windows.Point _dragStart;
    private bool _isDragging = false;
    private bool _hasPositioned = false;

    public ProcessCardViewModel ViewModel { get; } = new ProcessCardViewModel();

    public OverlayWindow()
    {
        InitializeComponent();
        DataContext = ViewModel;

        // Start invisible to prevent the frame-0 flash at (0,0).
        // The Loaded event positions and fades in the window once layout is complete
        // and ActualHeight is real.
        Opacity = 0;
        Loaded += OnFirstLoaded;
    }

    private void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;
        PositionNearTaskbar();
        _hasPositioned = true;

        // Fade in smoothly on first appearance
        var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(220));
        BeginAnimation(OpacityProperty, fadeIn);
    }

    /// <summary>
    /// Positions the window near the bottom-right corner of the primary screen.
    /// Only called when ActualHeight is valid (after layout/Loaded).
    /// </summary>
    private void PositionNearTaskbar()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 16;
        // ActualHeight is now accurate because we're in or after Loaded
        Top = workArea.Bottom - ActualHeight - 60;
    }

    /// <summary>
    /// Updates the displayed content and plays a fade transition.
    /// Safe to call before the window has fully appeared.
    /// </summary>
    public void ShowEntry(ProcessCardViewModel viewModel)
    {
        Dispatcher.Invoke(() =>
        {
            // Buffer the incoming data so we always display the latest values,
            // even if a fade is already in progress.
            ApplyViewModel(viewModel);
            UpdateStatusDot();

            // If a fade cycle is already running, let it finish — the data
            // is already updated so the fade-in will reveal the new content.
            if (_isFading) return;

            // Don't fade-out if the window is still invisible (startup state).
            double fromOpacity = _hasPositioned ? Opacity : 0.0;

            _isFading = true;
            var fadeOut = new DoubleAnimation(fromOpacity, 0.0, TimeSpan.FromMilliseconds(120));
            fadeOut.Completed += (_, _) =>
            {
                var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(180));
                fadeIn.Completed += (_, _) => _isFading = false;
                BeginAnimation(OpacityProperty, fadeIn);
            };
            BeginAnimation(OpacityProperty, fadeOut);
        });
    }

    private void ApplyViewModel(ProcessCardViewModel viewModel)
    {
        ViewModel.IsAllQuiet = viewModel.IsAllQuiet;
        ViewModel.ProcessName = viewModel.ProcessName;
        ViewModel.DisplayName = viewModel.DisplayName;
        ViewModel.Category = viewModel.Category;
        ViewModel.Summary = viewModel.Summary;
        ViewModel.TriggerReason = viewModel.TriggerReason;
        ViewModel.CpuPercent = viewModel.CpuPercent;
        ViewModel.MemoryPercent = viewModel.MemoryPercent;
        ViewModel.DiskPercent = viewModel.DiskPercent;
        ViewModel.HasKnowledge = viewModel.HasKnowledge;
        ViewModel.Publisher = viewModel.Publisher;
    }

    private void UpdateStatusDot()
    {
        StatusDot.Fill = ViewModel.IsAllQuiet
            ? (System.Windows.Media.Brush)FindResource("QuietAccentBrush")
            : (System.Windows.Media.Brush)FindResource("AccentBrush");
    }

    /// <summary>
    /// Allows dragging the window by holding the left mouse button on the card.
    /// </summary>
    private void RootBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _isDragging = true;
        CaptureMouse();
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_isDragging && e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
        {
            var pos = e.GetPosition(this);
            Left += pos.X - _dragStart.X;
            Top += pos.Y - _dragStart.Y;
        }
    }

    protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _isDragging = false;
        ReleaseMouseCapture();
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        _isPinned = !_isPinned;
        Topmost = _isPinned;
        PinButton.Content = _isPinned ? "📍" : "📌";
        PinButton.ToolTip = _isPinned ? "Unpin overlay" : "Pin overlay (keep on top)";
    }

    public bool IsPinned => _isPinned;
}
