using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIMeter.Core;

namespace AIMeter.App;

public sealed class OverlayPosition
{
    /// <summary>Right edge, so the stack stays anchored when providers appear or disappear.</summary>
    public double Right { get; set; }
    public double Top { get; set; }
}

public sealed class AppSettings
{
    public List<ProviderId> Connected { get; set; } = [];
    public int RefreshMinutes { get; set; } = 5;
    public double GaugeSize { get; set; } = 50;
    public double Opacity { get; set; } = 0.5;
    public bool ColorByUsage { get; set; } = true;
    public int AmberAt { get; set; } = 75;
    public int RedAt { get; set; } = 90;
    public bool DarkEdge { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public bool FirstRunDone { get; set; }
    /// <summary>Keyed by monitor layout, so each desk setup keeps its own spot.</summary>
    public Dictionary<string, OverlayPosition> Positions { get; set; } = [];

    public TimeSpan RefreshInterval => TimeSpan.FromMinutes(Math.Max(1, RefreshMinutes));
}

public sealed class ProviderState
{
    public UsageSnapshot? Last { get; set; }
    public bool SignedOut { get; set; }
    public DateTimeOffset? LastAttempt { get; set; }
    public string? LastError { get; set; }
}

public static class Storage
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T Load<T>(string path) where T : new()
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? new T();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Info($"could not read {Path.GetFileName(path)}: {ex.Message}");
        }
        return new T();
    }

    public static void Save<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
            File.Move(tmp, path, overwrite: true);
        }
        catch (IOException ex)
        {
            Log.Info($"could not save {Path.GetFileName(path)}: {ex.Message}");
        }
    }
}
