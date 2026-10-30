using System.IO;

namespace AIMeter.App;

/// <summary>All app data lives under %LOCALAPPDATA%\AIMeter (not roamed, not synced).</summary>
public static class AppPaths
{
    public static readonly string Root =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIMeter");

    public static readonly string WebView2 = Path.Combine(Root, "WebView2");
    public static readonly string Settings = Path.Combine(Root, "settings.json");
    public static readonly string State = Path.Combine(Root, "state.json");
    public static readonly string Log = Path.Combine(Root, "aimeter.log");
}

public static class Log
{
    private const long MaxBytes = 1_000_000;
    private static readonly object Gate = new();

    public static void Info(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Root);
                var info = new FileInfo(AppPaths.Log);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(AppPaths.Log, AppPaths.Log + ".old", overwrite: true);
                File.AppendAllText(AppPaths.Log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // Logging must never take the app down.
            }
        }
    }
}
