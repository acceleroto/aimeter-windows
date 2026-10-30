using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using AIMeter.Core;

namespace AIMeter.App;

/// <summary>
/// Quiet tray icon: no balloons or badges. The menu shows each provider's status and
/// holds the app's commands.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly AppController _app;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();

    public event Action? QuitRequested;

    public TrayIcon(AppController app)
    {
        _app = app;
        _icon = new NotifyIcon
        {
            Icon = DrawIcon(),
            Text = "AIMeter",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _menu.Opening += (_, _) => BuildMenu();
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) _app.ShowSettings();
        };
        // Left click opens the menu too; it's the only UI the tray has.
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            typeof(NotifyIcon).GetMethod("ShowContextMenu",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.Invoke(_icon, null);
        };
        _app.Changed += UpdateTooltip;
        UpdateTooltip();
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        var now = DateTimeOffset.Now;
        var any = false;
        foreach (var id in _app.Connected)
        {
            any = true;
            var name = ProviderInfo.Name(id);
            var state = _app.StateFor(id);
            if (state.SignedOut)
            {
                _menu.Items.Add($"{name}: sign-in expired — reconnect…", null, (_, _) => _app.Connect(id));
                continue;
            }
            var gauge = GaugeModel.Build(id, state.Last, false, _app.Settings.RefreshInterval, now);
            string text;
            if (state.Last is { } snap && gauge.State != GaugeState.NoData)
            {
                var plan = snap.Plan is null ? "" : $" ({snap.Plan})";
                text = $"{name}{plan}: {UsageText.Describe(snap, now)}";
                if (gauge.State == GaugeState.Stale) text += $" — updated {Ago(snap.FetchedAt, now)}";
            }
            else
            {
                text = state.LastError is null ? $"{name}: waiting for data" : $"{name}: couldn't refresh — {state.LastError}";
            }
            _menu.Items.Add(new ToolStripMenuItem(text) { Enabled = false });
        }
        if (!any) _menu.Items.Add("No providers connected — open settings…", null, (_, _) => _app.ShowSettings());

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Refresh now", null, async (_, _) => await _app.RefreshAllAsync());
        _menu.Items.Add(_app.MoveMode ? "Done moving" : "Move overlay…", null, (_, _) => _app.MoveMode = !_app.MoveMode);
        _menu.Items.Add("Settings…", null, (_, _) => _app.ShowSettings());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Quit AIMeter", null, (_, _) => QuitRequested?.Invoke());
    }

    private void UpdateTooltip()
    {
        var parts = _app.Gauges().Select(g =>
        {
            var abbr = ProviderInfo.Abbreviation(g.Provider);
            if (g.State == GaugeState.NoData) return $"{abbr} –";
            return g.Inner is { } inner ? $"{abbr} {inner:0}%/{g.Outer:0}%" : $"{abbr} {g.Outer:0}%";
        });
        var text = "AIMeter — " + string.Join(" · ", parts);
        if (!_app.Connected.Any()) text = "AIMeter";
        _icon.Text = text.Length > 127 ? text[..127] : text;
    }

    private static string Ago(DateTimeOffset then, DateTimeOffset now)
    {
        var d = now - then;
        return d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes} min ago"
            : d.TotalHours < 24 ? $"{(int)d.TotalHours} h ago"
            : $"{(int)d.TotalDays} d ago";
    }

    /// <summary>A small teal 270° arc, matching the overlay.</summary>
    private static Icon DrawIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(4, 4, 24, 24);
            using var track = new Pen(Color.FromArgb(160, 120, 128, 140), 5) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var value = new Pen(Color.FromArgb(0x5D, 0xCA, 0xA5), 5) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(track, rect, 135, 270);
            g.DrawArc(value, rect, 135, 170);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _app.Changed -= UpdateTooltip;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
