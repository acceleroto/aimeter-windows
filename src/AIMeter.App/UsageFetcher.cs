using System.Windows;
using System.Windows.Interop;
using AIMeter.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AIMeter.App;

/// <summary>
/// Background refresh: loads the provider's usage page in an offscreen window using the
/// saved sign-in, runs the fetch script, and tears everything down afterwards so the
/// app holds no browser memory between refreshes.
/// </summary>
public static class UsageFetcher
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);
    // Some sites bounce through their auth host even when signed in; only give up if we stay there.
    private static readonly TimeSpan SignInGrace = TimeSpan.FromSeconds(8);

    public static async Task<FetchResult> FetchAsync(ProviderWeb provider)
    {
        var view = new WebView2();
        var host = CreateOffscreenWindow(view);
        var page = new UsagePage(provider, view, pollForSpaNavigation: false);
        var done = new TaskCompletionSource<FetchResult>();
        var retried = false;

        page.Result += async r =>
        {
            if (r.Outcome == FetchOutcome.Failed && !retried)
            {
                // Providers occasionally return a 5xx for a moment; one retry covers it.
                retried = true;
                Log.Info($"{provider.Id}: {r.Error}; retrying");
                await Task.Delay(3000);
                page.TryRunScript(force: true);
                return;
            }
            done.TrySetResult(r);
        };
        page.SignInPageShown += async uri =>
        {
            await Task.Delay(SignInGrace);
            if (page.CurrentUri is { } now && provider.IsSignInPage(now))
                done.TrySetResult(FetchResult.SignedOut($"redirected to {now.Host}{now.AbsolutePath}"));
        };

        try
        {
            host.Show();
            await page.InitAsync();
            page.Navigate(provider.UsagePageUrl);
            var finished = await Task.WhenAny(done.Task, Task.Delay(Timeout));
            return finished == done.Task ? done.Task.Result : FetchResult.Fail("timed out");
        }
        catch (Exception ex)
        {
            return FetchResult.Fail(ex.Message);
        }
        finally
        {
            page.Stop();
            host.Close();
            view.Dispose();
        }
    }

    private static Window CreateOffscreenWindow(WebView2 view)
    {
        // Offscreen rather than hidden so the page renders and runs as if visible.
        var w = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            Width = 1000,
            Height = 800,
            Content = view,
        };
        w.SourceInitialized += (_, _) =>
            Native.AddExStyle(new WindowInteropHelper(w).Handle, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
        return w;
    }
}
