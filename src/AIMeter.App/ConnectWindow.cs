using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AIMeter.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AIMeter.App;

/// <summary>
/// Visible browser window for signing in to a provider. Closes itself as soon as usage
/// loads with the new sign-in.
/// </summary>
public sealed class ConnectWindow : Window
{
    private readonly ProviderWeb _provider;
    private readonly WebView2 _view = new();
    private readonly UsagePage _page;
    private readonly TextBlock _status;

    public UsageSnapshot? Snapshot { get; private set; }

    public ConnectWindow(ProviderWeb provider)
    {
        _provider = provider;
        var name = ProviderInfo.Name(provider.Id);
        Title = $"Connect {name} — AIMeter";
        Width = 1000;
        Height = 820;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _status = new TextBlock
        {
            Text = $"Sign in to {name}. This window closes by itself once your usage loads. " +
                   "If Google or Apple sign-in doesn't work here, use email sign-in.",
            TextWrapping = TextWrapping.Wrap,
            Padding = new Thickness(12, 8, 12, 8),
            Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6)),
            Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37)),
        };
        var dock = new DockPanel();
        DockPanel.SetDock(_status, Dock.Top);
        dock.Children.Add(_status);
        dock.Children.Add(_view);
        Content = dock;

        _page = new UsagePage(provider, _view, pollForSpaNavigation: true);
        _page.Result += OnResult;
        Loaded += async (_, _) =>
        {
            try
            {
                await _page.InitAsync();
                _page.Navigate(provider.SignInUrl);
            }
            catch (Exception ex)
            {
                _status.Text = "Couldn't start the browser: " + ex.Message;
                Log.Info($"{provider.Id}: connect window failed to start: {ex}");
            }
        };
        Closed += (_, _) =>
        {
            _page.Stop();
            _view.Dispose();
        };
    }

    private async void OnResult(FetchResult r)
    {
        switch (r.Outcome)
        {
            case FetchOutcome.Success:
                Snapshot = r.Snapshot;
                _status.Text = $"Connected. {UsageText.Describe(r.Snapshot!, DateTimeOffset.Now)}";
                _page.Stop();
                await Task.Delay(1200);
                DialogResult = true;
                break;
            case FetchOutcome.Failed:
                Log.Info($"{_provider.Id}: connect fetch failed: {r.Error}");
                _status.Text = $"Signed in, but usage didn't load yet ({r.Error}). Still trying…";
                break;
            case FetchOutcome.SignedOut:
                // Expected until the user finishes signing in.
                break;
        }
    }
}
