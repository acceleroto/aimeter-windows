# AIMeter for Windows — design

A Windows companion to the macOS AIMeter fork (https://github.com/acceleroto/aimeter).
Shows Claude, Cursor, and Codex usage as a small always-on-top desktop overlay.

## Overlay

- Vertical stack of arc gauges in the top-right corner. No background panel.
- Each gauge: 270° arc (gap at the bottom), number centered, provider label
  (`Cl`, `Cu`, `Cx`) just below the number in a smaller, dimmer font.
- Providers with two time windows get a double arc:
  - Claude: outer = weekly, inner = 5-hour. Center number = 5-hour.
  - Codex: same, if the usage response includes both windows (it appears to:
    `primary_window` / `secondary_window`).
  - Cursor: two monthly pools, Auto (+ Composer) and API. Outer = Auto,
    inner = API, center = whichever is fuller (running out of either blocks
    that kind of use). Free/Hobby has no API allowance, so it's a single Auto
    arc. The blended `totalPercentUsed` is not shown: it read 56% while Auto
    was at 100% and Cursor was refusing requests.
- Arc color: teal, amber at 75%, red at 90% (thresholds adjustable; can be
  turned off for a single color).
- Text and empty tracks get a thin dark edge (1–2 px, follows the shape, never
  a box) so they stay legible over light windows. Can be turned off.
- Text never has a background rectangle. Arcs must always show through.
- Default gauge size 50 px at 100% scaling, default opacity 50%; labels never
  smaller than 11 px. Size and opacity adjustable.
- Always on top. Fully click-through, never takes focus.
- Hides while a fullscreen app or game is in the foreground.
- Position adjustable: tray menu → "Move overlay…" makes it draggable until
  "Done". Position is remembered per monitor setup.

## Provider states

| State | Display |
|---|---|
| Fresh | Full color |
| Stale (last good data older than 2 refresh intervals) | Last numbers, dimmed to ~45% |
| Connected, no usable data (sign-in expired, first fetch pending, or a reset time has passed since the last good data) | Dashed ring, `–` in center, label below |
| Never connected / disconnected | Hidden; stack shrinks to fit |

If no providers are connected, the overlay is hidden and Settings opens on
first launch.

## Tray icon

Quiet: no badges, no notifications. Menu:
- Per-provider status line, e.g. "Codex: sign-in expired — reconnect…"
- Refresh now
- Move overlay…
- Settings…
- Quit

## Settings

- Connect / disconnect each provider
- Refresh interval (default 5 min); refresh after wake; pause while locked
- Gauge size, opacity, color thresholds, dark edge on/off
- Start with Windows (default on)

## Data (Option A: embedded browser)

- WebView2, one profile per provider, stored in `%LOCALAPPDATA%\AIMeter`
  (never in a synced folder).
- Sign-in in a visible WebView2 window; background refresh in a hidden one.
- Endpoints (from the macOS fork):
  - Cursor: `POST https://cursor.com/api/dashboard/get-current-period-usage`
    and `.../get-plan-info` (body `{}`), from `https://cursor.com/dashboard/spending`
  - Claude: usage JSON with `five_hour` / `seven_day` (`utilization`,
    `resets_at`), loaded by `https://claude.ai/settings/usage`
  - Codex: `GET https://chatgpt.com/backend-api/wham/usage`, from
    `https://chatgpt.com/codex/cloud/settings/analytics`
- Fallbacks, in order: capture page response → fetch from inside the page →
  DevTools protocol → local CLI tokens (Claude Code / Codex) → DOM scraping.
- No data leaves the machine.

### Spike results (2026-10-30, WebView2 156, hidden offscreen window)

All three work in the background with saved sign-ins. Data arrives 1–2 s after
the usage page loads. No bot checks were hit.

- **Claude**: page response and in-page fetch both work.
  `GET /api/organizations` → `GET /api/organizations/{uuid}/usage`.
  Use `five_hour.utilization` / `resets_at` and `seven_day.utilization` /
  `resets_at` (percent, 0–100). The page ends up at `/new#settings/usage`.
- **Codex**: page response works. In-page fetch of `/backend-api/wham/usage`
  returns 401 without a bearer token; fetching `/api/auth/session` in the page
  and retrying with its `accessToken` works (token stays in the page).
  `rate_limit.primary_window` / `secondary_window`, each with `used_percent`,
  `limit_window_seconds`, `reset_at` (unix seconds). On a Plus account only
  `primary_window` is present and it is the weekly window (604800 s), so pick
  arcs by `limit_window_seconds`, not by primary/secondary.
- **Cursor**: WebView2 cannot read POST response bodies from the page
  ("Unexpected HRESULT"), so use in-page fetch: `POST
  /api/dashboard/get-current-period-usage` (body `{}`, header
  `Connect-Protocol-Version: 1`) → `planUsage.autoPercentUsed`,
  `apiPercentUsed` (blended `totalPercentUsed` only as a fallback);
  `get-plan-info` → `planName`,
  `billingCycleEnd` (unix ms). `GET /api/usage-summary` carries the same
  percentages and can be read passively. Cursor occasionally returns 502/503
  for a moment; treat as a transient failure and retry.

Plan: in-page fetch is the primary method for all three (one code path);
captured page responses are the fallback.

## Stack

C# / .NET 10, WPF, WebView2, WinForms `NotifyIcon` for the tray (no extra
dependency). Velopack for install + updates via GitHub Releases. Windows only.

- `src/AIMeter.Core` — models, JSON parsers, gauge/state rules (no UI; tested)
- `src/AIMeter.App` — overlay, tray, settings, sign-in windows, background fetch
- `tests/AIMeter.Tests` — parser and state tests with made-up fixtures

Background refresh creates an offscreen WebView2 per provider, one at a time,
and closes it afterwards, so no browser processes stay running between
refreshes. App data: `%LOCALAPPDATA%\AIMeter` (`settings.json`, `state.json`,
`aimeter.log`, `WebView2\`).
