# Codex Usage Widget

A compact Windows widget for monitoring Codex subscription quota across multiple ChatGPT accounts.

<p align="center">
  <img src="docs/widget-preview.svg" width="460" alt="Codex Usage Widget preview" />
</p>

<p align="center">
  <img src="docs/analytics-preview.svg" width="760" alt="Codex Usage Widget analytics preview" />
</p>

## Features

- Multiple isolated ChatGPT/Codex accounts in one widget.
- Remaining 5-hour and weekly Codex quota with local reset countdowns.
- Expandable account cards for additional rate-limit buckets, credits, freshness, reconnect, and removal controls.
- 60-second automatic refresh with no overlapping refresh cycles.
- Always-on-top toggle, system tray support, optional Windows startup, remembered size and position.
- Native Windows 11 rounded outer corners and Mica backdrop, with graceful fallback on older Windows.
- Optional analytics view with local quota history, recent quota burn-rate estimates, lifetime/peak token activity, and recent daily token charts.
- Uses separate `CODEX_HOME` directories and Codex's Windows credential store (`keyring`); the widget does not collect account passwords or tokens.

## Download the latest Windows build

You do not need to compile it yourself.

1. Open the repository's **Actions** tab.
2. Open the newest successful **build** run on `main`.
3. Scroll to **Artifacts**.
4. Download **CodexUsageWidget-win-x64**.
5. Unzip it and run `CodexUsageWidget.exe`.

Repository Actions: https://github.com/mastachef/codex-usage-widget/actions

### Updating an existing copy

1. Quit the old widget from its tray icon.
2. Download the newest **CodexUsageWidget-win-x64** artifact using the steps above.
3. Unzip the new build.
4. Replace your old `CodexUsageWidget.exe` with the new one.
5. Launch the new EXE.

Your account profiles and usage history are stored under `%LOCALAPPDATA%/CodexUsageWidget`, so replacing the EXE does not normally remove your saved accounts/history.

## Build it yourself

The app requires Codex Desktop to be installed. A self-contained Windows build can be produced with:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

Then launch:

```text
publish/CodexUsageWidget.exe
```

The GitHub Actions workflow automatically creates the Windows artifact on pushes and pull requests.

## Accounts and sign-in

Click **Sign in**, finish the official browser sign-in, then use **Add account** for each additional account. Choose the intended account in the browser. If the browser reuses the wrong session, sign out there and reconnect that widget card.

**Copy sign-in link** creates a sign-in link without opening a browser, or copies the current pending link. Paste it into the desired browser/profile on the same PC and keep the widget running until sign-in completes.

Each account runs in its own profile directory under `%LOCALAPPDATA%/CodexUsageWidget/profiles`, isolated from the user's normal Codex profile. Credentials remain in Codex's credential store.

## Visual design

The widget keeps the normal resizable Windows frame for reliable snapping, resizing, minimize/maximize controls, and accessibility. On supported Windows 11 builds it asks DWM for rounded outer corners and a Mica system backdrop rather than using whole-window opacity, which would also fade text and controls.

## What the widget measures

This displays **Codex subscription limits**, not ordinary ChatGPT per-model message caps or API billing. Percentages are remaining quota. The app only displays backend-provided values; missing values remain unavailable, and failed reads retain the last snapshot while marking it unavailable.

Each account uses one idle Codex app-server process. No model turns are started by the widget.

### Analytics

The analytics view samples the quota percentages already returned by Codex once per refresh and stores them locally under `%LOCALAPPDATA%/CodexUsageWidget/history`. It also calls `account/usage/read` for account token-activity summaries and daily buckets when that endpoint is available.

The displayed quota burn rate is an **observed estimate**, not a token-to-quota conversion. Subscription quota can be weighted by factors that are not exposed as a simple public percentage-per-token formula.

## Controls

- Drag the normal Windows title bar to move the widget.
- Resize from any edge or corner.
- **On top** toggles always-on-top behavior.
- **Refresh** reloads all account limits immediately.
- Double-click the tray icon to show the widget.
- The tray menu includes **Start with Windows** and **Quit**.

## Tests

```powershell
publish/CodexUsageWidget.exe --self-test
```

Tests cover quota math, missing/expired values, bucket preservation, compact/expanded rendering, isolated logged-out profiles, and OAuth start/cancel. Live authenticated quota reads require the account owner to complete sign-in.

## Protocol references

- https://learn.chatgpt.com/docs/app-server
- https://learn.chatgpt.com/docs/auth
