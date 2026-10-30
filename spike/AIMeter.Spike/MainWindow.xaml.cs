using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace AIMeter.Spike;

public partial class MainWindow : Window
{
    private static readonly string DataRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIMeterSpike");
    private static readonly string CapturesDir = Path.Combine(DataRoot, "captures");
    private static readonly string LogFile = Path.Combine(DataRoot, "spike.log");

    private static readonly Regex InterestingKey =
        new("percent|utiliz|reset|plan|used|limit|window|remaining|credit|balance", RegexOptions.IgnoreCase);

    private CoreWebView2Environment? _env;

    public MainWindow()
    {
        InitializeComponent();
        Directory.CreateDirectory(CapturesDir);
        foreach (var p in Providers.All)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new TextBlock { Text = p.Name, Width = 70, VerticalAlignment = VerticalAlignment.Center });
            var signIn = new Button { Content = "Sign in", Padding = new Thickness(10, 2, 10, 2) };
            signIn.Click += async (_, _) => await OpenSignInAsync(p);
            var test = new Button { Content = "Test in background", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 0) };
            test.Click += async (_, _) => await RunBackgroundTestAsync(p);
            row.Children.Add(signIn);
            row.Children.Add(test);
            ProviderRows.Children.Add(row);
        }
        Loaded += async (_, _) =>
        {
            _env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(DataRoot, "WebView2"));
            Log("spike", $"WebView2 {_env.BrowserVersionString}, data in {DataRoot}");
            // "--test <id>" runs one background test and exits, for unattended runs.
            var args = Environment.GetCommandLineArgs();
            var i = Array.IndexOf(args, "--test");
            if (i >= 0 && i + 1 < args.Length && Providers.All.FirstOrDefault(p => p.Id == args[i + 1]) is { } target)
            {
                await RunBackgroundTestAsync(target);
                Close();
            }
        };
    }

    private async Task OpenSignInAsync(ProviderDef p)
    {
        if (_env is null) return;
        var session = new BrowserSession(p, _env, hidden: false, msg => Log(p.Name, msg));
        session.UsageCaptured += (method, status, body) => Report(p, method, status, body);
        session.ProbeMessage += msg => Log(p.Name, "probe: " + msg);
        await session.InitAsync();
        session.Navigate(p.SignInUrl);
        Log(p.Name, "sign-in window opened; after signing in, it will be sent to the usage page");
        // Once signed in, providers usually land on their home page; send the user to the usage page.
        var sentToUsage = false;
        session.NavigationDone += (url, ok) =>
        {
            if (sentToUsage || !ok) return;
            if (url.Contains("login", StringComparison.OrdinalIgnoreCase) || url.Contains("auth", StringComparison.OrdinalIgnoreCase)) return;
            if (url.StartsWith(p.SignInUrl, StringComparison.OrdinalIgnoreCase)) return;
            sentToUsage = true;
            session.Navigate(p.UsagePageUrl);
        };
    }

    private async Task<int> RunBackgroundTestAsync(ProviderDef p)
    {
        if (_env is null) return 0;
        Log(p.Name, "--- background test start ---");
        var sw = Stopwatch.StartNew();
        var good = 0;
        var session = new BrowserSession(p, _env, hidden: true, msg => Log(p.Name, msg));
        session.UsageCaptured += (method, status, body) =>
        {
            if (status == 200) good++;
            Report(p, method, status, body);
        };
        session.ProbeMessage += msg => Log(p.Name, "probe: " + msg);

        var firstNav = new TaskCompletionSource();
        session.NavigationDone += (_, _) => firstNav.TrySetResult();
        await session.InitAsync();
        session.Navigate(p.UsagePageUrl);
        await Task.WhenAny(firstNav.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        // Let the page's own requests (and any sign-in redirects) settle.
        await Task.Delay(TimeSpan.FromSeconds(6));
        Log(p.Name, $"page settled at {session.CurrentUrl} after {sw.Elapsed.TotalSeconds:0.0}s; passive captures so far: {good}");

        await session.RunProbeAsync();
        await Task.Delay(TimeSpan.FromSeconds(8));
        session.Close();
        Log(p.Name, $"--- background test end: {good} good capture(s), {sw.Elapsed.TotalSeconds:0.0}s ---");
        return good;
    }

    private async void TestAll_Click(object sender, RoutedEventArgs e)
    {
        TestAllButton.IsEnabled = false;
        var results = new List<string>();
        foreach (var p in Providers.All)
            results.Add($"{p.Name}: {(await RunBackgroundTestAsync(p) > 0 ? "OK" : "NO DATA")}");
        Log("spike", "SUMMARY  " + string.Join("   ", results));
        TestAllButton.IsEnabled = true;
    }

    private void OpenCaptures_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(CapturesDir) { UseShellExecute = true });

    private void Report(ProviderDef p, string method, int status, string body)
    {
        var file = Path.Combine(CapturesDir, $"{p.Id}-{DateTime.Now:HHmmss-fff}-{(method.StartsWith("probe") ? "probe" : "passive")}.json");
        File.WriteAllText(file, body);
        Log(p.Name, $"CAPTURE {method} http={status} bytes={body.Length} -> {Path.GetFileName(file)}");
        try
        {
            using var doc = JsonDocument.Parse(body);
            var lines = new List<string>();
            Flatten(doc.RootElement, "", lines);
            foreach (var line in lines.Where(l => InterestingKey.IsMatch(l.Split('=')[0])).Take(20))
                Log(p.Name, "    " + line);
        }
        catch (JsonException)
        {
            Log(p.Name, "    (not JSON) " + body[..Math.Min(body.Length, 200)].ReplaceLineEndings(" "));
        }
    }

    private static void Flatten(JsonElement el, string path, List<string> lines)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                    Flatten(prop.Value, path.Length == 0 ? prop.Name : $"{path}.{prop.Name}", lines);
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in el.EnumerateArray().Take(5))
                    Flatten(item, $"{path}[{i++}]", lines);
                break;
            default:
                lines.Add($"{path}={el}");
                break;
        }
    }

    private void Log(string who, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} [{who}] {message}";
        File.AppendAllText(LogFile, line + Environment.NewLine);
        Dispatcher.Invoke(() =>
        {
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        });
    }
}
