using System.Text.Json;
using System.Windows.Threading;
using AIMeter.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AIMeter.App;

public static class WebViewEnvironment
{
    private static Task<CoreWebView2Environment>? _env;

    /// <summary>One browser environment for the app; each provider gets its own profile in it.</summary>
    public static Task<CoreWebView2Environment> GetAsync() =>
        _env ??= CoreWebView2Environment.CreateAsync(null, AppPaths.WebView2);

    public static async Task InitAsync(WebView2 view, ProviderId provider)
    {
        var env = await GetAsync();
        var options = env.CreateCoreWebView2ControllerOptions();
        options.ProfileName = provider.ToString().ToLowerInvariant();
        await view.EnsureCoreWebView2Async(env, options);
    }
}

/// <summary>
/// Drives one WebView2 showing a provider's site: runs the provider's fetch script once
/// the page is on the provider's own host, and reports the result.
/// </summary>
public sealed class UsagePage
{
    private readonly ProviderWeb _provider;
    private readonly WebView2 _view;
    private readonly DispatcherTimer _poll;
    private bool _scriptRunning;

    /// <summary>A fetch script finished.</summary>
    public event Action<FetchResult>? Result;

    /// <summary>The browser landed on a sign-in page.</summary>
    public event Action<Uri>? SignInPageShown;

    public UsagePage(ProviderWeb provider, WebView2 view, bool pollForSpaNavigation)
    {
        _provider = provider;
        _view = view;
        // Sign-in flows often finish with in-page (history API) navigation, which raises no
        // NavigationCompleted, so the connect window also checks the URL periodically.
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _poll.Tick += (_, _) => TryRunScript();
        if (pollForSpaNavigation) _poll.Start();
    }

    public Uri? CurrentUri => Uri.TryCreate(_view.CoreWebView2?.Source, UriKind.Absolute, out var u) ? u : null;

    public async Task InitAsync()
    {
        await WebViewEnvironment.InitAsync(_view, _provider.Id);
        var core = _view.CoreWebView2;
        core.WebMessageReceived += OnMessage;
        core.NavigationCompleted += (_, e) =>
        {
            if (!e.IsSuccess) Log.Info($"{_provider.Id}: navigation failed ({e.WebErrorStatus}) at {core.Source}");
            TryRunScript();
        };
    }

    public void Navigate(string url) => _view.CoreWebView2.Navigate(url);

    public void Stop() => _poll.Stop();

    /// <summary>Runs the fetch script if the page is on the provider's own, non-sign-in page.</summary>
    public void TryRunScript(bool force = false)
    {
        if (_view.CoreWebView2 is null || (_scriptRunning && !force)) return;
        var uri = CurrentUri;
        if (uri is null || uri.Scheme != Uri.UriSchemeHttps) return;
        if (_provider.IsSignInPage(uri))
        {
            SignInPageShown?.Invoke(uri);
            return;
        }
        _scriptRunning = true;
        _ = _view.CoreWebView2.ExecuteScriptAsync(_provider.FetchScript);
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Only trust messages from the provider's own origin.
        if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var source) || !_provider.IsOwnHost(source)) return;
        try
        {
            using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
            var root = doc.RootElement;
            if (!root.TryGetProperty("kind", out var k) || k.GetString() != "aimeter") return;
            _scriptRunning = false;
            Result?.Invoke(_provider.Interpret(root, DateTimeOffset.Now));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            _scriptRunning = false;
            Log.Info($"{_provider.Id}: bad message: {ex.Message}");
        }
    }
}
