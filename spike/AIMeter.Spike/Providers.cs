namespace AIMeter.Spike;

/// <summary>
/// What the spike needs to know about each provider: where to sign in, which page
/// loads usage, which responses carry usage, and a script that fetches usage from
/// inside the page. Probe scripts report via chrome.webview.postMessage and never
/// post auth tokens.
/// </summary>
public sealed record ProviderDef(
    string Id,
    string Name,
    string SignInUrl,
    string UsagePageUrl,
    string[] Hosts,
    Func<Uri, bool> IsUsageResponse,
    string ProbeScript);

public static class Providers
{
    private const string PostHelper = """
        const post = (o) => window.chrome.webview.postMessage(JSON.stringify(o));
        """;

    public static readonly ProviderDef Claude = new(
        Id: "claude",
        Name: "Claude",
        SignInUrl: "https://claude.ai/login",
        UsagePageUrl: "https://claude.ai/settings/usage",
        Hosts: ["claude.ai", "www.claude.ai"],
        IsUsageResponse: u => u.AbsolutePath.StartsWith("/api/organizations/", StringComparison.OrdinalIgnoreCase)
                              && u.AbsolutePath.EndsWith("/usage", StringComparison.OrdinalIgnoreCase),
        ProbeScript: $$"""
            (async () => {
              {{PostHelper}}
              try {
                const r = await fetch('/api/organizations', { credentials: 'include' });
                if (!r.ok) { post({ kind: 'probe', url: r.url, status: r.status, body: (await r.text()).slice(0, 300) }); return; }
                const orgs = await r.json();
                post({ kind: 'probe-info', note: `organizations: ${orgs.length}` });
                for (const o of orgs) {
                  const r2 = await fetch(`/api/organizations/${o.uuid}/usage`, { credentials: 'include' });
                  post({ kind: 'probe', url: r2.url, status: r2.status, body: await r2.text(),
                         note: 'capabilities=' + (o.capabilities || []).join(',') });
                }
              } catch (e) { post({ kind: 'probe-error', error: String(e) }); }
            })();
            """);

    public static readonly ProviderDef Cursor = new(
        Id: "cursor",
        Name: "Cursor",
        SignInUrl: "https://cursor.com/dashboard/spending",
        UsagePageUrl: "https://cursor.com/dashboard/spending",
        Hosts: ["cursor.com", "www.cursor.com"],
        IsUsageResponse: u => u.AbsolutePath.StartsWith("/api/", StringComparison.OrdinalIgnoreCase),
        ProbeScript: $$"""
            (async () => {
              {{PostHelper}}
              for (const ep of ['/api/dashboard/get-current-period-usage', '/api/dashboard/get-plan-info']) {
                try {
                  const r = await fetch(ep, { method: 'POST', credentials: 'include', body: '{}',
                    headers: { 'Content-Type': 'application/json', 'Connect-Protocol-Version': '1' } });
                  post({ kind: 'probe', url: r.url, status: r.status, body: await r.text() });
                } catch (e) { post({ kind: 'probe-error', error: ep + ': ' + String(e) }); }
              }
            })();
            """);

    public static readonly ProviderDef Codex = new(
        Id: "codex",
        Name: "Codex",
        SignInUrl: "https://chatgpt.com/auth/login",
        UsagePageUrl: "https://chatgpt.com/codex/cloud/settings/analytics",
        Hosts: ["chatgpt.com", "www.chatgpt.com"],
        IsUsageResponse: u => u.AbsolutePath.StartsWith("/backend-api/wham/usage", StringComparison.OrdinalIgnoreCase),
        ProbeScript: $$"""
            (async () => {
              {{PostHelper}}
              try {
                let r = await fetch('/backend-api/wham/usage', { credentials: 'include', cache: 'no-store' });
                if (r.status === 401 || r.status === 403) {
                  post({ kind: 'probe-info', note: `plain fetch got ${r.status}; retrying with session bearer token` });
                  const s = await fetch('/api/auth/session', { credentials: 'include' });
                  const token = s.ok ? (await s.json()).accessToken : null;
                  if (!token) { post({ kind: 'probe-info', note: 'no session token (signed out?)' }); return; }
                  r = await fetch('/backend-api/wham/usage', { credentials: 'include', cache: 'no-store',
                    headers: { Authorization: 'Bearer ' + token } });
                }
                post({ kind: 'probe', url: r.url, status: r.status, body: await r.text() });
              } catch (e) { post({ kind: 'probe-error', error: String(e) }); }
            })();
            """);

    public static readonly ProviderDef[] All = [Claude, Cursor, Codex];
}
