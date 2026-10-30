using System.Text.Json;
using AIMeter.Core;

namespace AIMeter.App;

/// <summary>
/// Per-provider web details: where to sign in, which page to load, and a script that
/// fetches usage from inside that page (so the provider's own cookies are used and
/// nothing leaves the browser except the usage JSON). Scripts post exactly one
/// message: { kind: "aimeter", signedIn, error?, ...data }.
/// </summary>
public sealed class ProviderWeb
{
    public required ProviderId Id { get; init; }
    public required string SignInUrl { get; init; }
    public required string UsagePageUrl { get; init; }
    public required string[] Hosts { get; init; }
    public required string FetchScript { get; init; }
    public required Func<JsonElement, DateTimeOffset, UsageSnapshot> Parse { get; init; }

    public bool IsOwnHost(Uri uri) => Hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

    /// <summary>True for pages that mean "not signed in": login paths or another (auth) host.</summary>
    public bool IsSignInPage(Uri uri) =>
        !IsOwnHost(uri)
        || uri.AbsolutePath.Contains("/login", StringComparison.OrdinalIgnoreCase)
        || uri.AbsolutePath.StartsWith("/auth", StringComparison.OrdinalIgnoreCase);

    public FetchResult Interpret(JsonElement message, DateTimeOffset now)
    {
        var signedIn = !message.TryGetProperty("signedIn", out var s) || s.ValueKind != JsonValueKind.False;
        var error = message.TryGetProperty("error", out var e) ? e.GetString() : null;
        if (!signedIn) return FetchResult.SignedOut(error ?? "signed out");
        if (error is not null) return FetchResult.Fail(error);
        try
        {
            return FetchResult.Ok(Parse(message, now));
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return FetchResult.Fail("unexpected response: " + ex.Message);
        }
    }

    private const string Post = """
        const post = (o) => window.chrome.webview.postMessage(JSON.stringify(Object.assign({ kind: 'aimeter' }, o)));
        const denied = (r) => r.status === 401 || r.status === 403;
        """;

    public static readonly ProviderWeb Claude = new()
    {
        Id = ProviderId.Claude,
        SignInUrl = "https://claude.ai/login",
        UsagePageUrl = "https://claude.ai/settings/usage",
        Hosts = ["claude.ai", "www.claude.ai"],
        FetchScript = "(async () => {" + Post + """
              try {
                const r = await fetch('/api/organizations', { credentials: 'include', cache: 'no-store' });
                if (denied(r)) return post({ signedIn: false, error: 'organizations ' + r.status });
                if (!r.ok) return post({ signedIn: true, error: 'organizations HTTP ' + r.status });
                const orgs = await r.json();
                const org = orgs.find(o => (o.capabilities || []).includes('chat')) || orgs[0];
                if (!org) return post({ signedIn: true, error: 'no organization' });
                const u = await fetch(`/api/organizations/${org.uuid}/usage`, { credentials: 'include', cache: 'no-store' });
                if (denied(u)) return post({ signedIn: false, error: 'usage ' + u.status });
                if (!u.ok) return post({ signedIn: true, error: 'usage HTTP ' + u.status });
                post({ signedIn: true, capabilities: org.capabilities || [], usage: await u.json() });
              } catch (e) { post({ signedIn: true, error: String(e) }); }
            })();
            """,
        Parse = (m, now) => UsageParsers.ParseClaude(
            m.GetProperty("usage"),
            m.TryGetProperty("capabilities", out var c) ? c.EnumerateArray().Select(x => x.GetString() ?? "") : [],
            now),
    };

    public static readonly ProviderWeb Cursor = new()
    {
        Id = ProviderId.Cursor,
        SignInUrl = "https://cursor.com/dashboard/spending",
        UsagePageUrl = "https://cursor.com/dashboard/spending",
        Hosts = ["cursor.com", "www.cursor.com"],
        // The page's own POST responses can't be read by WebView2, so always fetch directly.
        FetchScript = "(async () => {" + Post + """
              const call = (ep) => fetch(ep, { method: 'POST', credentials: 'include', body: '{}',
                headers: { 'Content-Type': 'application/json', 'Connect-Protocol-Version': '1' } });
              try {
                const r = await call('/api/dashboard/get-current-period-usage');
                if (denied(r)) return post({ signedIn: false, error: 'usage ' + r.status });
                if (!r.ok) return post({ signedIn: true, error: 'usage HTTP ' + r.status });
                const usage = await r.json();
                let plan = null;
                try { const p = await call('/api/dashboard/get-plan-info'); if (p.ok) plan = await p.json(); } catch (_) {}
                post({ signedIn: true, usage, plan });
              } catch (e) { post({ signedIn: true, error: String(e) }); }
            })();
            """,
        Parse = (m, now) => UsageParsers.ParseCursor(
            m.GetProperty("usage"),
            m.TryGetProperty("plan", out var p) && p.ValueKind == JsonValueKind.Object ? p : null,
            now),
    };

    public static readonly ProviderWeb Codex = new()
    {
        Id = ProviderId.Codex,
        SignInUrl = "https://chatgpt.com/auth/login",
        UsagePageUrl = "https://chatgpt.com/codex/cloud/settings/analytics",
        Hosts = ["chatgpt.com", "www.chatgpt.com"],
        // The usage endpoint wants the session's bearer token; it is used here and never posted out.
        FetchScript = "(async () => {" + Post + """
              try {
                const s = await fetch('/api/auth/session', { credentials: 'include', cache: 'no-store' });
                const token = s.ok ? (await s.json()).accessToken : null;
                if (!token) return post({ signedIn: false, error: 'no session' });
                const r = await fetch('/backend-api/wham/usage', { credentials: 'include', cache: 'no-store',
                  headers: { Authorization: 'Bearer ' + token } });
                if (denied(r)) return post({ signedIn: false, error: 'usage ' + r.status });
                if (!r.ok) return post({ signedIn: true, error: 'usage HTTP ' + r.status });
                post({ signedIn: true, usage: await r.json() });
              } catch (e) { post({ signedIn: true, error: String(e) }); }
            })();
            """,
        Parse = (m, now) => UsageParsers.ParseCodex(m.GetProperty("usage"), now),
    };

    public static readonly IReadOnlyList<ProviderWeb> All = [Claude, Codex, Cursor];

    public static ProviderWeb For(ProviderId id) => All.First(p => p.Id == id);
}
