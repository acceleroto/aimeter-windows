using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AIMeter.Spike;

/// <summary>
/// A WebView2 in its own window, using the provider's own profile. Logs usage
/// responses the page loads ("passive") and results of the in-page probe ("probe").
/// </summary>
public sealed class BrowserSession
{
    private readonly ProviderDef _provider;
    private readonly CoreWebView2Environment _env;
    private readonly Action<string> _log;
    private readonly Window _window;
    private readonly WebView2 _webView = new();

    public event Action<string, int, string>? UsageCaptured; // method, status, body
    public event Action<string, bool>? NavigationDone;        // url, success
    public event Action<string>? ProbeMessage;               // non-usage probe notes/errors

    public BrowserSession(ProviderDef provider, CoreWebView2Environment env, bool hidden, Action<string> log)
    {
        _provider = provider;
        _env = env;
        _log = log;
        _window = new Window
        {
            Title = $"AIMeter spike — {provider.Name}",
            Width = 1000,
            Height = 800,
            Content = _webView,
        };
        if (hidden)
        {
            // Offscreen rather than hidden, so the page renders and runs normally.
            _window.WindowStyle = WindowStyle.None;
            _window.ShowInTaskbar = false;
            _window.ShowActivated = false;
            _window.WindowStartupLocation = WindowStartupLocation.Manual;
            _window.Left = -32000;
            _window.Top = -32000;
        }
    }

    public string CurrentUrl => _webView.CoreWebView2?.Source ?? "";

    public async Task InitAsync()
    {
        _window.Show();
        var options = _env.CreateCoreWebView2ControllerOptions();
        options.ProfileName = _provider.Id;
        await _webView.EnsureCoreWebView2Async(_env, options);

        var core = _webView.CoreWebView2;
        core.WebResourceResponseReceived += OnResponseReceived;
        core.WebMessageReceived += OnWebMessage;
        core.NavigationCompleted += (_, e) =>
        {
            _log($"navigated: {core.Source} (ok={e.IsSuccess}, http={e.HttpStatusCode})");
            NavigationDone?.Invoke(core.Source, e.IsSuccess);
        };
    }

    public void Navigate(string url) => _webView.CoreWebView2.Navigate(url);

    public Task RunProbeAsync() => _webView.CoreWebView2.ExecuteScriptAsync(_provider.ProbeScript);

    public void Close() => _window.Close();

    private async void OnResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)) return;
        if (!_provider.Hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)) return;
        if (!_provider.IsUsageResponse(uri)) return;
        try
        {
            var headers = string.Join(", ", e.Request.Headers
                .Where(h => !h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
                            && !h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                .Select(h => $"{h.Key}: {h.Value}"));
            _log($"request: {e.Request.Method} {uri.AbsolutePath} -> {e.Response.StatusCode} | {headers}");
            if (e.Request.Content is { } reqBody)
            {
                using var r = new StreamReader(reqBody);
                var text = await r.ReadToEndAsync();
                if (text.Length > 0) _log($"request body: {text[..Math.Min(text.Length, 300)]}");
            }
            using var stream = await e.Response.GetContentAsync();
            if (stream is null)
            {
                _log($"passive: {uri.AbsolutePath} had no readable body");
                return;
            }
            using var reader = new StreamReader(stream);
            var body = await reader.ReadToEndAsync();
            UsageCaptured?.Invoke($"passive {uri.AbsolutePath}", e.Response.StatusCode, body);
        }
        catch (Exception ex)
        {
            _log($"passive: {uri.AbsolutePath} read failed: {ex.Message}");
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
            var root = doc.RootElement;
            var kind = root.GetProperty("kind").GetString();
            if (kind == "probe")
            {
                var url = root.GetProperty("url").GetString() ?? "";
                var path = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath : url;
                var note = root.TryGetProperty("note", out var n) ? $" [{n.GetString()}]" : "";
                UsageCaptured?.Invoke($"probe {path}{note}", root.GetProperty("status").GetInt32(),
                    root.GetProperty("body").GetString() ?? "");
            }
            else
            {
                var text = root.TryGetProperty("note", out var n) ? n.GetString()
                    : root.TryGetProperty("error", out var err) ? "error: " + err.GetString() : kind;
                ProbeMessage?.Invoke(text ?? "");
            }
        }
        catch (Exception ex)
        {
            _log($"bad web message: {ex.Message}");
        }
    }
}
