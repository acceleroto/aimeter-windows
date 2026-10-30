using System.Text.Json;
using AIMeter.Core;

namespace AIMeter.Tests;

// Fixtures are trimmed, made-up responses shaped like the real ones from the spike.
public class UsageParsersTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 7, 0, 0, TimeSpan.Zero);

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public void Claude_reads_five_hour_and_weekly()
    {
        var usage = Json("""
            {
              "five_hour": { "utilization": 7.0, "resets_at": "2026-10-30T07:40:00.313391+00:00", "limit_dollars": null },
              "seven_day": { "utilization": 37.0, "resets_at": "2026-10-31T21:00:00.313408+00:00" },
              "extra_usage": { "utilization": null }
            }
            """);

        var s = UsageParsers.ParseClaude(usage, ["claude_pro", "chat"], Now);

        Assert.Equal("Pro", s.Plan);
        Assert.Equal(2, s.Windows.Count);
        var fiveHour = s.Windows.Single(w => w.Duration == TimeSpan.FromHours(5));
        Assert.Equal(7.0, fiveHour.Percent);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 7, 40, 0, 313, TimeSpan.Zero), fiveHour.ResetsAt!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Equal(37.0, s.Windows.Single(w => w.Duration == TimeSpan.FromDays(7)).Percent);
    }

    [Fact]
    public void Claude_without_windows_throws()
    {
        Assert.Throws<FormatException>(() => UsageParsers.ParseClaude(Json("""{ "five_hour": null }"""), [], Now));
    }

    [Fact]
    public void Codex_classifies_windows_by_length_not_position()
    {
        // On Plus, primary_window is the weekly window and secondary is null.
        var usage = Json("""
            {
              "plan_type": "plus",
              "rate_limit": {
                "primary_window": { "used_percent": 44, "limit_window_seconds": 604800, "reset_after_seconds": 465023, "reset_at": 1793798482 },
                "secondary_window": null
              }
            }
            """);

        var s = UsageParsers.ParseCodex(usage, Now);

        Assert.Equal("Plus", s.Plan);
        var w = Assert.Single(s.Windows);
        Assert.Equal("Weekly", w.Name);
        Assert.Equal(TimeSpan.FromDays(7), w.Duration);
        Assert.Equal(44, w.Percent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793798482), w.ResetsAt);
    }

    [Fact]
    public void Codex_reads_both_windows_when_present()
    {
        var usage = Json("""
            {
              "rate_limit": {
                "primary_window": { "used_percent": 12, "limit_window_seconds": 18000, "reset_at": 1793700000 },
                "secondary_window": { "used_percent": 50, "limit_window_seconds": 604800, "reset_at": 1793798482 }
              }
            }
            """);

        var s = UsageParsers.ParseCodex(usage, Now);

        Assert.Equal(2, s.Windows.Count);
        Assert.Contains(s.Windows, w => w.Name == "5-hour" && w.Percent == 12);
        Assert.Contains(s.Windows, w => w.Name == "Weekly" && w.Percent == 50);
    }

    [Fact]
    public void Cursor_free_plan_shows_only_the_auto_pool()
    {
        // Free has no API allowance, so a full Auto pool means you're blocked even though
        // the blended total says 56%.
        var usage = Json("""
            { "billingCycleEnd": "1794951713140",
              "planUsage": { "totalSpend": 113, "autoPercentUsed": 100, "apiPercentUsed": 0, "totalPercentUsed": 56.49999999999999 } }
            """);
        var plan = Json("""
            { "planInfo": { "planName": "Free", "price": "Free", "billingCycleEnd": "1794951713140" } }
            """);

        var s = UsageParsers.ParseCursor(usage, plan, Now);

        Assert.Equal("Free", s.Plan);
        var w = Assert.Single(s.Windows);
        Assert.Equal("Auto", w.Name);
        Assert.Equal(100, w.Percent);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1794951713140), w.ResetsAt);
    }

    [Fact]
    public void Cursor_paid_plan_shows_auto_and_api_pools()
    {
        var usage = Json("""
            { "planUsage": { "autoPercentUsed": 30, "apiPercentUsed": 85, "totalPercentUsed": 57.5 } }
            """);
        var plan = Json("""{ "planInfo": { "planName": "Pro" } }""");

        var s = UsageParsers.ParseCursor(usage, plan, Now);

        Assert.Equal(2, s.Windows.Count);
        Assert.Contains(s.Windows, w => w.Name == "Auto" && w.Percent == 30);
        Assert.Contains(s.Windows, w => w.Name == "API" && w.Percent == 85);
    }

    [Fact]
    public void Cursor_without_plan_info_still_parses()
    {
        var s = UsageParsers.ParseCursor(Json("""{ "planUsage": { "totalPercentUsed": 10 } }"""), null, Now);
        Assert.Null(s.Plan);
        Assert.Null(s.Windows[0].ResetsAt);
    }

    [Fact]
    public void Percent_is_clamped()
    {
        var s = UsageParsers.ParseCursor(Json("""{ "planUsage": { "totalPercentUsed": 140 } }"""), null, Now);
        Assert.Equal(100, s.Windows[0].Percent);
    }
}
