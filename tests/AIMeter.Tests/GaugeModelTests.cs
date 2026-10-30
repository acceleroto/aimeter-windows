using AIMeter.Core;

namespace AIMeter.Tests;

public class GaugeModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 7, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private static UsageSnapshot Claude(DateTimeOffset fetchedAt, DateTimeOffset? fiveHourReset = null) => new(
        ProviderId.Claude, "Pro",
        [
            new UsageWindow("5-hour", TimeSpan.FromHours(5), 42, fiveHourReset ?? Now.AddHours(2)),
            new UsageWindow("Weekly", TimeSpan.FromDays(7), 78, Now.AddDays(2)),
        ],
        fetchedAt);

    [Fact]
    public void Two_windows_make_a_double_arc_with_the_short_window_in_the_center()
    {
        var g = GaugeModel.Build(ProviderId.Claude, Claude(Now), false, Interval, Now);
        Assert.Equal(GaugeState.Fresh, g.State);
        Assert.Equal(78, g.Outer);
        Assert.Equal(42, g.Inner);
        Assert.Equal(42, g.Center);
    }

    [Fact]
    public void One_window_makes_a_single_arc()
    {
        var s = new UsageSnapshot(ProviderId.Cursor, null,
            [new UsageWindow("Billing period", TimeSpan.FromDays(30), 18, null)], Now);
        var g = GaugeModel.Build(ProviderId.Cursor, s, false, Interval, Now);
        Assert.Equal(18, g.Outer);
        Assert.Null(g.Inner);
        Assert.Equal(18, g.Center);
    }

    [Fact]
    public void Parallel_pools_show_the_fuller_one_in_the_center()
    {
        var month = TimeSpan.FromDays(30);
        var s = new UsageSnapshot(ProviderId.Cursor, "Pro",
            [new UsageWindow("Auto", month, 30, null), new UsageWindow("API", month, 85, null)], Now);
        var g = GaugeModel.Build(ProviderId.Cursor, s, false, Interval, Now);
        Assert.Equal(30, g.Outer);
        Assert.Equal(85, g.Inner);
        Assert.Equal(85, g.Center);
    }

    [Fact]
    public void Older_than_two_intervals_is_stale()
    {
        Assert.Equal(GaugeState.Fresh, GaugeModel.Build(ProviderId.Claude, Claude(Now.AddMinutes(-9)), false, Interval, Now).State);
        Assert.Equal(GaugeState.Stale, GaugeModel.Build(ProviderId.Claude, Claude(Now.AddMinutes(-11)), false, Interval, Now).State);
    }

    [Fact]
    public void Signed_out_or_missing_is_no_data()
    {
        Assert.Equal(GaugeState.NoData, GaugeModel.Build(ProviderId.Claude, Claude(Now), true, Interval, Now).State);
        Assert.Equal(GaugeState.NoData, GaugeModel.Build(ProviderId.Claude, null, false, Interval, Now).State);
    }

    [Fact]
    public void A_passed_reset_time_is_no_data()
    {
        var s = Claude(Now.AddHours(-3), fiveHourReset: Now.AddMinutes(-1));
        Assert.Equal(GaugeState.NoData, GaugeModel.Build(ProviderId.Claude, s, false, Interval, Now).State);
    }
}
