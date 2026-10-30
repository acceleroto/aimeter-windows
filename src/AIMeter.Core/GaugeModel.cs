using System.Globalization;

namespace AIMeter.Core;

public enum GaugeState
{
    /// <summary>Recent data; full color.</summary>
    Fresh,
    /// <summary>Last refresh failed but the old numbers are still meaningful; dimmed.</summary>
    Stale,
    /// <summary>Connected, but nothing trustworthy to show; dashed ring.</summary>
    NoData,
}

/// <summary>What one overlay gauge should draw.</summary>
/// <param name="Outer">Longest window's percent (e.g. weekly).</param>
/// <param name="Inner">Shortest window's percent when there are two (e.g. 5-hour); otherwise null.</param>
/// <param name="Center">
/// Number shown in the middle. For time windows of different lengths (Claude's 5-hour vs
/// weekly) it's the shorter one, which moves fastest. For parallel pools of the same length
/// (Cursor's Auto vs API) it's the fuller one, since running out of either blocks you.
/// </param>
public sealed record GaugeModel(ProviderId Provider, GaugeState State, double? Outer, double? Inner, double? Center)
{
    public static GaugeModel Build(ProviderId provider, UsageSnapshot? snapshot, bool signedOut,
        TimeSpan refreshInterval, DateTimeOffset now)
    {
        var noData = new GaugeModel(provider, GaugeState.NoData, null, null, null);
        if (signedOut || snapshot is null || snapshot.Windows.Count == 0) return noData;

        var ordered = snapshot.Windows.OrderByDescending(w => w.Duration).ToList();
        var outer = ordered[0];
        var inner = ordered.Count > 1 ? ordered[^1] : null;

        // Once a window has reset, its old percentage is wrong rather than merely old.
        if (outer.ResetsAt <= now || inner?.ResetsAt <= now) return noData;

        var state = now - snapshot.FetchedAt > refreshInterval * 2 ? GaugeState.Stale : GaugeState.Fresh;
        var center = inner is null ? outer.Percent
            : inner.Duration == outer.Duration ? Math.Max(inner.Percent, outer.Percent)
            : inner.Percent;
        return new GaugeModel(provider, state, outer.Percent, inner?.Percent, center);
    }
}

public static class UsageText
{
    /// <summary>One-line summary for the tray menu, e.g. "5-hour 7% · Weekly 37% (resets Sat 2:00 PM)".</summary>
    public static string Describe(UsageSnapshot s, DateTimeOffset now, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var parts = s.Windows
            .OrderBy(w => w.Duration)
            .Select(w => $"{w.Name} {Math.Round(w.Percent).ToString(culture)}%"
                         + (w.ResetsAt is { } r ? $" (resets {FormatReset(r, now, culture)})" : ""));
        return string.Join(" · ", parts);
    }

    public static string FormatReset(DateTimeOffset resetsAt, DateTimeOffset now, CultureInfo culture)
    {
        var local = resetsAt.ToLocalTime();
        var time = local.ToString("t", culture);
        var days = (local.Date - now.ToLocalTime().Date).Days;
        return days switch
        {
            0 => time,
            1 => "tomorrow " + time,
            < 7 => local.ToString("ddd ", culture) + time,
            _ => local.ToString("MMM d", culture),
        };
    }
}
