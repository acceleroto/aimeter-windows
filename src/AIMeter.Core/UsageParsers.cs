using System.Globalization;
using System.Text.Json;

namespace AIMeter.Core;

/// <summary>
/// Turns provider JSON into snapshots. Field names come from the 2026-10-30 spike;
/// see DESIGN.md. Parsers throw <see cref="FormatException"/> when no usable window is found.
/// </summary>
public static class UsageParsers
{
    private static readonly TimeSpan FiveHours = TimeSpan.FromHours(5);
    private static readonly TimeSpan OneWeek = TimeSpan.FromDays(7);

    /// <param name="usage">Body of GET /api/organizations/{uuid}/usage.</param>
    /// <param name="capabilities">The organization's capabilities, e.g. "claude_pro".</param>
    public static UsageSnapshot ParseClaude(JsonElement usage, IEnumerable<string> capabilities, DateTimeOffset now)
    {
        var windows = new List<UsageWindow>();
        AddClaudeWindow(usage, "five_hour", "5-hour", FiveHours, windows);
        AddClaudeWindow(usage, "seven_day", "Weekly", OneWeek, windows);
        if (windows.Count == 0) throw new FormatException("Claude usage had no five_hour or seven_day window.");

        string? plan = null;
        foreach (var c in capabilities)
        {
            if (c.StartsWith("claude_", StringComparison.OrdinalIgnoreCase))
            {
                var tier = c["claude_".Length..];
                plan = tier.Length == 0 ? null : char.ToUpperInvariant(tier[0]) + tier[1..];
                break;
            }
        }
        return new UsageSnapshot(ProviderId.Claude, plan, windows, now);
    }

    private static void AddClaudeWindow(JsonElement usage, string key, string name, TimeSpan duration, List<UsageWindow> into)
    {
        if (!usage.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object) return;
        if (!TryGetDouble(w, "utilization", out var pct)) return;
        into.Add(new UsageWindow(name, duration, Clamp(pct), TryGetDate(w, "resets_at")));
    }

    /// <param name="usage">Body of GET /backend-api/wham/usage.</param>
    public static UsageSnapshot ParseCodex(JsonElement usage, DateTimeOffset now)
    {
        var windows = new List<UsageWindow>();
        if (usage.TryGetProperty("rate_limit", out var rl) && rl.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "primary_window", "secondary_window" })
            {
                if (!rl.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object) continue;
                if (!TryGetDouble(w, "used_percent", out var pct)) continue;
                // Classify by length, not by primary/secondary: on Plus, primary is the weekly window.
                var duration = TryGetDouble(w, "limit_window_seconds", out var secs)
                    ? TimeSpan.FromSeconds(secs)
                    : key == "primary_window" ? FiveHours : OneWeek;
                DateTimeOffset? resets = TryGetDouble(w, "reset_at", out var unix)
                    ? DateTimeOffset.FromUnixTimeSeconds((long)unix)
                    : TryGetDouble(w, "reset_after_seconds", out var after) ? now.AddSeconds(after) : null;
                windows.Add(new UsageWindow(NameForDuration(duration), duration, Clamp(pct), resets));
            }
        }
        if (windows.Count == 0) throw new FormatException("Codex usage had no rate-limit windows.");

        string? plan = usage.TryGetProperty("plan_type", out var pt) && pt.ValueKind == JsonValueKind.String
            ? Capitalize(pt.GetString()!)
            : null;
        return new UsageSnapshot(ProviderId.Codex, plan, windows, now);
    }

    /// <summary>
    /// Cursor has two separate monthly pools: Auto (+ Composer) and API (named models).
    /// Running out of either blocks that kind of use, so both are reported; the blended
    /// totalPercentUsed hides an exhausted pool and is only a fallback. Free/Hobby plans
    /// have no API allowance, so that pool is left out for them.
    /// </summary>
    /// <param name="periodUsage">Body of POST /api/dashboard/get-current-period-usage.</param>
    /// <param name="planInfo">Body of POST /api/dashboard/get-plan-info, if it loaded.</param>
    public static UsageSnapshot ParseCursor(JsonElement periodUsage, JsonElement? planInfo, DateTimeOffset now)
    {
        if (!periodUsage.TryGetProperty("planUsage", out var pu) || pu.ValueKind != JsonValueKind.Object)
            throw new FormatException("Cursor usage had no planUsage.");

        string? plan = null;
        DateTimeOffset? cycleEnd = TryGetDouble(periodUsage, "billingCycleEnd", out var endMs)
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)endMs)
            : null;
        if (planInfo is { } pi && pi.TryGetProperty("planInfo", out var info))
        {
            if (info.TryGetProperty("planName", out var pn) && pn.ValueKind == JsonValueKind.String) plan = pn.GetString();
            if (TryGetDouble(info, "billingCycleEnd", out var ms)) cycleEnd = DateTimeOffset.FromUnixTimeMilliseconds((long)ms);
        }

        var month = TimeSpan.FromDays(30);
        var windows = new List<UsageWindow>();
        if (TryGetDouble(pu, "autoPercentUsed", out var auto))
            windows.Add(new UsageWindow("Auto", month, Clamp(auto), cycleEnd));
        var hasApiAllowance = plan is null
            || !(plan.Equals("Free", StringComparison.OrdinalIgnoreCase) || plan.Equals("Hobby", StringComparison.OrdinalIgnoreCase));
        if (hasApiAllowance && TryGetDouble(pu, "apiPercentUsed", out var api))
            windows.Add(new UsageWindow("API", month, Clamp(api), cycleEnd));
        if (windows.Count == 0 && TryGetDouble(pu, "totalPercentUsed", out var total))
            windows.Add(new UsageWindow("Total", month, Clamp(total), cycleEnd));
        if (windows.Count == 0) throw new FormatException("Cursor usage had no usage percentages.");

        return new UsageSnapshot(ProviderId.Cursor, plan, windows, now);
    }

    private static string NameForDuration(TimeSpan d) =>
        d <= TimeSpan.FromHours(6) ? $"{Math.Round(d.TotalHours)}-hour"
        : d <= TimeSpan.FromDays(8) ? "Weekly"
        : "Monthly";

    private static double Clamp(double pct) => Math.Clamp(pct, 0, 100);

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>Reads a number, or a string holding a number (Cursor sends some as strings).</summary>
    private static bool TryGetDouble(JsonElement obj, string key, out double value)
    {
        value = 0;
        if (!obj.TryGetProperty(key, out var el)) return false;
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetDouble(out value),
            JsonValueKind.String => double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }

    private static DateTimeOffset? TryGetDate(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(el.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
            ? d
            : null;
}
