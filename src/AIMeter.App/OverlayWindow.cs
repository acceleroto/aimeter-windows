using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AIMeter.Core;

namespace AIMeter.App;

/// <summary>
/// The always-on-top stack of gauges. Click-through and never focused, except in
/// move mode, where it can be dragged.
/// </summary>
public sealed class OverlayWindow : Window
{
    private const double EdgeGap = 16;
    private const double Spacing = 6;

    private readonly StackPanel _stack = new() { Orientation = Orientation.Vertical };
    private readonly Border _frame;
    private readonly DispatcherTimer _watch;
    private AppSettings _settings;
    private IntPtr _hwnd;
    private bool _hasGauges;
    private bool _hiddenForFullscreen;
    private bool _moveMode;

    /// <summary>Raised after the user drags the overlay to a new spot.</summary>
    public event Action? Moved;

    public OverlayWindow(AppSettings settings)
    {
        _settings = settings;
        Title = "AIMeter overlay";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _frame = new Border
        {
            Child = _stack,
            Padding = new Thickness(4),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
            Background = Brushes.Transparent,
            // Windows enforces a minimum window width; hug the right edge so the gauges
            // sit where the position says, not wherever the minimum width puts them.
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Content = _frame;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.AddExStyle(_hwnd, Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED
                                     | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
        };
        SizeChanged += (_, _) => ApplyPosition();
        MouseLeftButtonDown += (_, e) =>
        {
            if (!_moveMode || e.ButtonState != MouseButtonState.Pressed) return;
            DragMove();
            SavePosition();
        };

        // Hide for fullscreen apps, and keep re-asserting topmost (other topmost windows can bury ours).
        _watch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _watch.Tick += (_, _) => WatchTick();
        _watch.Start();
    }

    public bool MoveMode
    {
        get => _moveMode;
        set
        {
            _moveMode = value;
            if (_hwnd != IntPtr.Zero)
            {
                if (value) Native.RemoveExStyle(_hwnd, Native.WS_EX_TRANSPARENT);
                else Native.AddExStyle(_hwnd, Native.WS_EX_TRANSPARENT);
            }
            _frame.Background = value ? new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x88, 0x96)) : Brushes.Transparent;
            _frame.BorderBrush = value ? new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent;
            Cursor = value ? Cursors.SizeAll : null;
            // In move mode, stay visible even with nothing connected, so there's something to drag.
            UpdateVisibility();
        }
    }

    public void Render(IReadOnlyList<GaugeModel> gauges, AppSettings settings)
    {
        _settings = settings;
        _stack.Children.Clear();
        for (var i = 0; i < gauges.Count; i++)
        {
            var g = GaugeVisual.Create(gauges[i], settings);
            if (i > 0) g.Margin = new Thickness(0, Spacing, 0, 0);
            _stack.Children.Add(g);
        }
        _stack.Opacity = Math.Clamp(settings.Opacity, 0.2, 1);
        _hasGauges = gauges.Count > 0;
        UpdateVisibility();
        ApplyPosition();
    }

    /// <summary>Monitor layout key: positions are remembered per desk setup.</summary>
    private static string LayoutKey() =>
        $"{SystemParameters.VirtualScreenLeft:0},{SystemParameters.VirtualScreenTop:0}," +
        $"{SystemParameters.VirtualScreenWidth:0}x{SystemParameters.VirtualScreenHeight:0}";

    private void ApplyPosition()
    {
        if (ActualWidth <= 0) return;
        var work = SystemParameters.WorkArea;
        double right, top;
        if (_settings.Positions.TryGetValue(LayoutKey(), out var p))
        {
            right = p.Right;
            top = p.Top;
        }
        else
        {
            right = work.Right - EdgeGap;
            top = work.Top + EdgeGap;
        }
        // Keep it on screen if monitors changed.
        var vl = SystemParameters.VirtualScreenLeft;
        var vt = SystemParameters.VirtualScreenTop;
        var vr = vl + SystemParameters.VirtualScreenWidth;
        var vb = vt + SystemParameters.VirtualScreenHeight;
        Left = Math.Clamp(right - ActualWidth, vl, Math.Max(vl, vr - ActualWidth));
        Top = Math.Clamp(top, vt, Math.Max(vt, vb - ActualHeight));
    }

    private void SavePosition()
    {
        _settings.Positions[LayoutKey()] = new OverlayPosition { Right = Left + ActualWidth, Top = Top };
        Moved?.Invoke();
    }

    private void UpdateVisibility()
    {
        var shouldShow = (_hasGauges || _moveMode) && !_hiddenForFullscreen;
        if (shouldShow && !IsVisible) Show();
        else if (!shouldShow && IsVisible) Hide();
    }

    private void WatchTick()
    {
        if (_hwnd == IntPtr.Zero) return;
        var fullscreen = !_moveMode && Native.IsFullscreenAppOnSameMonitor(_hwnd);
        if (fullscreen != _hiddenForFullscreen)
        {
            _hiddenForFullscreen = fullscreen;
            UpdateVisibility();
        }
        if (IsVisible) Native.BringToTopmost(_hwnd);
    }
}
