using System.Net.Http;
using Velopack;
using Velopack.Sources;

namespace AIMeter.App;

/// <summary>
/// Quiet auto-update from GitHub Releases. Updates download in the background and are
/// installed when AIMeter quits, or right away from the tray menu. Does nothing when
/// running from a build folder instead of an installed copy.
/// </summary>
public sealed class Updater
{
    public const string RepoUrl = "https://github.com/acceleroto/aimeter-windows";
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);

    private readonly UpdateManager _manager = new(new GithubSource(RepoUrl, null, false));
    private UpdateInfo? _ready;

    public event Action? Changed;

    /// <summary>Version that's downloaded and waiting to be installed, if any.</summary>
    public string? ReadyVersion => _ready?.TargetFullRelease.Version.ToString();

    public async void Start()
    {
        if (!_manager.IsInstalled) return;
        while (_ready is null)
        {
            await CheckAsync();
            await Task.Delay(CheckEvery);
        }
    }

    private async Task CheckAsync()
    {
        try
        {
            var info = await _manager.CheckForUpdatesAsync();
            if (info is null) return;
            await _manager.DownloadUpdatesAsync(info);
            _ready = info;
            Log.Info($"update {ReadyVersion} downloaded");
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.IO.IOException or InvalidOperationException)
        {
            Log.Info($"update check failed: {ex.Message}");
        }
    }

    /// <summary>Installs the downloaded update and restarts AIMeter. Doesn't return.</summary>
    public void InstallNow()
    {
        if (_ready is not null) _manager.ApplyUpdatesAndRestart(_ready.TargetFullRelease);
    }

    /// <summary>Call while quitting: installs a downloaded update once this process exits.</summary>
    public void InstallOnExit()
    {
        if (_ready is not null)
            _manager.WaitExitThenApplyUpdates(_ready.TargetFullRelease, silent: true, restart: false);
    }
}
