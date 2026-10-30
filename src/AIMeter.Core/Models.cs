namespace AIMeter.Core;

public enum ProviderId
{
    Claude,
    Cursor,
    Codex,
}

/// <summary>One usage limit, e.g. Claude's 5-hour window or Cursor's billing period.</summary>
/// <param name="Duration">Length of the window; used to order arcs (longest is outer).</param>
/// <param name="Percent">0–100.</param>
/// <param name="ResetsAt">When the window resets, if known.</param>
public sealed record UsageWindow(string Name, TimeSpan Duration, double Percent, DateTimeOffset? ResetsAt);

public sealed record UsageSnapshot(
    ProviderId Provider,
    string? Plan,
    IReadOnlyList<UsageWindow> Windows,
    DateTimeOffset FetchedAt);

public enum FetchOutcome
{
    Success,
    SignedOut,
    Failed,
}

public sealed record FetchResult(FetchOutcome Outcome, UsageSnapshot? Snapshot, string? Error)
{
    public static FetchResult Ok(UsageSnapshot s) => new(FetchOutcome.Success, s, null);
    public static FetchResult SignedOut(string why) => new(FetchOutcome.SignedOut, null, why);
    public static FetchResult Fail(string why) => new(FetchOutcome.Failed, null, why);
}

public static class ProviderInfo
{
    public static string Name(ProviderId p) => p switch
    {
        ProviderId.Claude => "Claude",
        ProviderId.Cursor => "Cursor",
        ProviderId.Codex => "Codex",
        _ => p.ToString(),
    };

    public static string Abbreviation(ProviderId p) => p switch
    {
        ProviderId.Claude => "Cl",
        ProviderId.Cursor => "Cu",
        ProviderId.Codex => "Cx",
        _ => p.ToString()[..2],
    };
}
