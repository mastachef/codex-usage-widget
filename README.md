# Codex Usage Widget

A compact Windows widget for monitoring Codex subscription quota across multiple ChatGPT accounts.

![Codex Usage Widget](docs/screenshot.png)

## Features

- Multiple isolated ChatGPT/Codex accounts in one widget.
- Remaining 5-hour and weekly Codex quota with local reset countdowns.
- Expandable account cards for additional rate-limit buckets, credits, freshness, reconnect, and removal controls.
- 60-second automatic refresh with no overlapping refresh cycles.
- Always-on-top toggle, system tray support, optional Windows startup, remembered size and position.
- Uses separate `CODEX_HOME` directories and Codex's Windows credential store (`keyring`); the widget does not collect account passwords or tokens.

## Download / run

The app requires Codex Desktop to be installed. A self-contained Windows build can be produced with:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

Then launch:

```text
publish/CodexUsageWidget.exe
```

The GitHub Actions workflow also builds a Windows artifact automatically on pushes and pull requests.

## Accounts and sign-in

Click **Sign in**, finish the official browser sign-in, then use **Add account** for each additional account. Choose the intended account in the browser. If the browser reuses the wrong session, sign out there and reconnect that widget card.

**Copy sign-in link** creates a sign-in link without opening a browser, or copies the current pending link. Paste it into the desired browser/profile on the same PC and keep the widget running until sign-in completes.

Each account runs in its own profile directory under `%LOCALAPPDATA%/CodexUsageWidget/profiles`, isolated from the user's normal Codex profile. Credentials remain in Codex's credential store.

## What the widget measures

This displays **Codex subscription limits**, not ordinary ChatGPT per-model message caps or API billing. Percentages are remaining quota. The app only displays backend-provided values; missing values remain unavailable, and failed reads retain the last snapshot while marking it unavailable.

Each account uses one idle Codex app-server process. No model turns are started by the widget.

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
