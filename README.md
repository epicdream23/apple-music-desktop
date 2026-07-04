# Apple Music Desktop

An unofficial, lightweight Apple Music desktop app for Windows 10/11 — a frameless
[WebView2](https://developer.microsoft.com/microsoft-edge/webview2/) wrapper around the
[Apple Music web player](https://music.apple.com) with a **liquid glass** UI.

No browser tabs, no URL bar, no clutter. Just music.

## Features

- 🪟 **Frameless window** with native Windows 11 rounded corners, drop shadow, Snap Layouts and Aero Snap
- 🧊 **Liquid glass window controls** — a floating translucent capsule with backdrop blur and saturation that picks up the colors of the artwork behind it
- 🤸 **Dynamic dodge** — the capsule automatically slides out of the way (with a springy animation) when Apple Music shows its own icons in the top-right corner, and springs back when they disappear. Occlusion- and opacity-aware, so hover-hidden buttons and covered elements don't trigger it
- 🚫 **No scrollbars** — wheel/keyboard scrolling still works, the draggable slider is hidden
- 🖱️ Drag the window from the capsule or the top edge, double-click to maximize, right-click for the system menu
- 🎬 Proper fullscreen for music videos and fullscreen lyrics
- 💾 Remembers window size/position; login persists between launches
- 🔐 DRM playback works (WebView2 = Chromium/Edge engine), Apple ID login popups stay in-app, external links open in your default browser
- 🎮 **Discord Rich Presence** — "Listening to Apple Music" with song title, artist, album art, live progress bar and an "In Apple Music ansehen" button (the *Listen Along* button is a Spotify-exclusive partner feature and not available to third-party apps)
- 📌 Pin button to keep the window always on top (also bypasses driver background-fps caps)
- 📦 Single ~1 MB exe (framework-dependent)

## Requirements

- Windows 10/11 (x64)
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (preinstalled on most systems)
- [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (preinstalled on Windows 11)

## Download

Grab `Apple Music.exe` from the [Releases](../../releases) page and put it wherever you like.
Optionally create a Start Menu shortcut so it shows up in Windows search.

## Build from source

```powershell
dotnet publish AppleMusic.csproj -c Release -o dist
```

The single-file exe lands in `dist/`. Requires the .NET 8 SDK (or newer).

## Configuration

- **Storefront**: the app opens `https://music.apple.com/de/home` (German storefront).
  Change the `HomeUrl` constant at the top of [`Program.cs`](Program.cs) to your country,
  e.g. `https://music.apple.com/us/home`.
- **Glass look**: all capsule styling lives in the injected CSS inside the `GlassScript`
  string in [`Program.cs`](Program.cs) — blur strength, tint, sizes and the dodge
  animation curve are plain CSS values.
- **Discord presence**: enabled by default when Discord is running. To turn it off, set
  `"DiscordRpc": false` in `%LOCALAPPDATA%\AppleMusicPlayer\window.json` (created after
  the first run). You can also point `"DiscordClientId"` at your own Discord application
  if you want a custom activity name.

## How it works

A borderless WinForms window hosts a WebView2 control. The window caption is removed via
`WM_NCCALCSIZE` while keeping the native resize frame, Snap support and shadow. The
window controls are injected into the Apple Music page itself as a `position: fixed`
overlay using `AddScriptToExecuteOnDocumentCreated`, which is what makes real
`backdrop-filter` glass over the app content possible. Dragging uses WebView2's
non-client region support (CSS `app-region: drag`), and the capsule talks to the host
via `chrome.webview.postMessage` for minimize/maximize/close.

## Disclaimer

This is an unofficial community project and is **not affiliated with, endorsed or
sponsored by Apple Inc.** Apple Music is a trademark of Apple Inc. The app is a thin
viewer around the official Apple Music web player; it does not modify, intercept or
circumvent anything — an Apple Music subscription is required for playback.

## License

[MIT](LICENSE)
