using System.Windows;
using System.Windows.Threading;
using AIMeter.Core;
using Microsoft.Win32;

namespace AIMeter.App;

/// <summary>
/// Owns settings and per-provider state, schedules refreshes, and keeps the overlay and
/// tray in sync. Everything runs on the UI thread.
/// </summary>
public sealed class AppController
{
    private readonly DispatcherTimer _refreshTimer = new();
    // Re-render now and then so gauges turn stale/no-data on time even without a refresh.
    private readonly DispatcherTimer _renderTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly OverlayWindow _overlay;
    private bool _refreshing;
    private bool _locked;
    private SettingsWindow? _settingsWindow;

    public AppSettings Settings { get; }
    public Dictionary<ProviderId, ProviderState> States { get; }

    /// <summary>Raised whenever something visible changed.</summary>
    public event Action? Changed;

    public AppController()
    {
        Settings = Storage.Load<AppSettings>(AppPaths.Settings);
        States = Storage.Load<Dictionary<ProviderId, ProviderState>>(AppPaths.State);
        _overlay = new OverlayWindow(Settings);
        _overlay.Moved += SaveSettings;

        _refreshTimer.Tick += async (_, _) => await RefreshAllAsync();
        _renderTimer.Tick += (_, _) => Render();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    public IEnumerable<ProviderId> Connected => Settings.Connected.OrderBy(p => p);

    public bool MoveMode
    {
        get => _overlay.MoveMode;
        set => _overlay.MoveMode = value;
    }

    public void Start()
    {
        if (!Settings.FirstRunDone)
        {
            Settings.FirstRunDone = true;
            Settings.StartWithWindows = true;
            SaveSettings();
        }
        StartupRegistration.Apply(Settings.StartWithWindows);

        Render();
        _refreshTimer.Interval = Settings.RefreshInterval;
        _refreshTimer.Start();
        _renderTimer.Start();

        if (!Settings.Connected.Any()) ShowSettings();
        else _ = RefreshAllAsync();
    }

    public void Stop()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _refreshTimer.Stop();
        _renderTimer.Stop();
        _overlay.Close();
    }

    public async Task RefreshAllAsync()
    {
        if (_refreshing || _locked) return;
        _refreshing = true;
        try
        {
            // One at a time keeps the browser's memory and CPU spike small.
            foreach (var id in Connected.ToList())
            {
                var result = await UsageFetcher.FetchAsync(ProviderWeb.For(id));
                if (!Settings.Connected.Contains(id)) continue; // disconnected meanwhile
                Apply(id, result);
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void Apply(ProviderId id, FetchResult result)
    {
        var state = StateFor(id);
        state.LastAttempt = DateTimeOffset.Now;
        switch (result.Outcome)
        {
            case FetchOutcome.Success:
                state.Last = result.Snapshot;
                state.SignedOut = false;
                state.LastError = null;
                break;
            case FetchOutcome.SignedOut:
                state.SignedOut = true;
                state.LastError = result.Error;
                break;
            default:
                state.LastError = result.Error;
                break;
        }
        Log.Info($"{id}: {result.Outcome}{(result.Error is null ? "" : " — " + result.Error)}");
        Storage.Save(AppPaths.State, States);
        Render();
    }

    public ProviderState StateFor(ProviderId id)
    {
        if (!States.TryGetValue(id, out var s)) States[id] = s = new ProviderState();
        return s;
    }

    public void Connect(ProviderId id)
    {
        var window = new ConnectWindow(ProviderWeb.For(id));
        if (_settingsWindow is { IsVisible: true }) window.Owner = _settingsWindow;
        if (window.ShowDialog() == true && window.Snapshot is { } snapshot)
        {
            if (!Settings.Connected.Contains(id)) Settings.Connected.Add(id);
            SaveSettings();
            Apply(id, FetchResult.Ok(snapshot));
        }
    }

    public async Task DisconnectAsync(ProviderId id)
    {
        Settings.Connected.Remove(id);
        States.Remove(id);
        SaveSettings();
        Storage.Save(AppPaths.State, States);
        Render();
        await ClearSignInAsync(id);
    }

    /// <summary>Forgets the provider's cookies and storage, so disconnecting really signs out.</summary>
    private static async Task ClearSignInAsync(ProviderId id)
    {
        var view = new Microsoft.Web.WebView2.Wpf.WebView2();
        var host = new Window
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            Left = -32000, Top = -32000, Width = 10, Height = 10, Content = view,
        };
        try
        {
            host.Show();
            await WebViewEnvironment.InitAsync(view, id);
            await view.CoreWebView2.Profile.ClearBrowsingDataAsync();
            Log.Info($"{id}: sign-in data cleared");
        }
        catch (Exception ex)
        {
            Log.Info($"{id}: could not clear sign-in data: {ex.Message}");
        }
        finally
        {
            host.Close();
            view.Dispose();
        }
    }

    /// <summary>Saves settings and applies anything that depends on them.</summary>
    public void SettingsChanged()
    {
        SaveSettings();
        StartupRegistration.Apply(Settings.StartWithWindows);
        if (_refreshTimer.Interval != Settings.RefreshInterval)
        {
            _refreshTimer.Interval = Settings.RefreshInterval; // also restarts the countdown
        }
        Render();
    }

    private void SaveSettings() => Storage.Save(AppPaths.Settings, Settings);

    public IReadOnlyList<GaugeModel> Gauges()
    {
        var now = DateTimeOffset.Now;
        return Connected
            .Select(id =>
            {
                var s = StateFor(id);
                return GaugeModel.Build(id, s.Last, s.SignedOut, Settings.RefreshInterval, now);
            })
            .ToList();
    }

    private void Render()
    {
        _overlay.Render(Gauges(), Settings);
        Changed?.Invoke();
    }

    public void ShowSettings()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        // Give the network a moment to come back after sleep.
        Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(15));
            await RefreshAllAsync();
        });
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            if (e.Reason == SessionSwitchReason.SessionLock)
            {
                _locked = true;
            }
            else if (e.Reason == SessionSwitchReason.SessionUnlock)
            {
                _locked = false;
                var oldest = Connected.Select(id => StateFor(id).LastAttempt ?? DateTimeOffset.MinValue).DefaultIfEmpty().Min();
                if (DateTimeOffset.Now - oldest > Settings.RefreshInterval) await RefreshAllAsync();
            }
        });
    }
}
