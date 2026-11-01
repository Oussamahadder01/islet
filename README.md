# Islet

A Dynamic Island for Windows, made to stay out of the way. It hides above the
top edge of the screen. Rest the pointer on the top-centre edge for a moment
and a liquid-glass island drops down. Move away and it retracts.

## Build and run

No SDK is needed. The build uses the C# compiler, WPF and shader compiler that
ship with Windows:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
.\bin\Islet.exe
```

Errors go to `%LOCALAPPDATA%\Islet\islet.log`.

## Install (always on)

```powershell
.\bin\Islet.exe --install
```

Run this again after rebuilding to upgrade. It installs per user and needs no
administrator rights:

| Piece | Purpose |
| --- | --- |
| `%LOCALAPPDATA%\Programs\Islet\Islet.exe` | The installed copy; the project's `bin` stays a dev build. |
| Task Scheduler task `Islet` | Starts at sign-in, plus a time trigger that re-checks every minute. Normal priority, no time limit, runs on battery. |
| Hot standby (`Islet.exe --standby`) | A second process (about 9 MB, 0% CPU) blocked on the single-instance mutex. If the island process dies, even by a forced kill, the standby takes over in under a second and raises a new standby. |
| Start menu shortcut | Find "Islet" like any app. |
| Settings → Apps entry | Uninstall runs `Islet.exe --uninstall`, which removes the task, shortcut, entry and folder (settings in `HKCU\Software\Islet` are kept). |

Measured recovery: the island returns 0.4–0.65 s after its process is killed.
A killed standby is replaced within seconds. If every Islet process is killed,
the scheduler restores it within a minute (50 s observed).

- A new standby is raised only after the island has run stably for 30 s, so a
  crash at startup cannot loop; the scheduler paces those retries.
- With always-on enabled the tray offers **Restart** instead of Exit. To stop
  Islet, untick **Start with Windows (always on)**: that pauses the task and
  dismisses the standby, and then Exit appears.

The tray icon also holds the display options: always show a compact island,
system alerts, song-change alerts and the web search engine.

## What it does

| Dynamic Island feature | Islet |
| --- | --- |
| Now Playing | Album art and a live audio spectrum. Expanded: title, artist, a seekable timeline, previous/play/next, and click the art to open the player. |
| Lyrics | The **CC** button in the player shows synced lyrics karaoke-style (previous, current and next line), from [LRCLIB](https://lrclib.net). Unsynced lyrics scroll over the track's duration. Only the title, artist, album and duration are sent, and only while lyrics are on. Results are cached in `%LOCALAPPDATA%\Islet\lyrics`; misses are retried after a day, network failures after 30 s. |
| Play Spotify | With Spotify installed but closed, the Music tab offers **Play**: it starts Spotify minimized, waits for it, and resumes the last track (falling back to the media play key). |
| Live Activities | Timer (presets, pause, +1 min, alarm when done) and stopwatch. |
| Two activities at once | The second live activity splits off as a bubble that merges back like liquid. |
| Charging / low battery | Plug and unplug alerts, plus warnings at 20% and 10%. |
| AirPods connected | Bluetooth connect alerts with battery level when the device reports it. |
| Silent switch | Mute / unmute alert for the default output device. |
| Sound | A **Sound** tab to choose the **output** and **input** devices (click one to pick from a list). It also has volume and mute for both. Switching changes the Windows default device (console and multimedia roles, as Windows' sound settings do); a separate communications device is left alone. The music player has a mini volume slider, the mouse wheel adjusts the volume, and the sliders follow the keyboard volume keys. |
| Privacy dots | Orange (microphone) and green (camera) dots while any app uses them; the Home view names the apps. |
| Tap to expand | Hover expands. Tabs and the mouse wheel switch between Home, Music, Timer and Stopwatch. |

Phone-only features (calls, Face ID, Apple Pay, AirDrop, third-party Live
Activities) have no Windows equivalent and are not included.

## Search and pinned apps

- **Search** opens with **Ctrl+Alt+Space** from anywhere, or the search tab.
  It matches every app in the Start menu's app list (desktop and Store apps,
  accent-insensitive, initials such as "vsc" included). Text that looks like a
  website gets an "Open" row. The last row searches the web with the engine
  chosen in the tray (Google, Bing or DuckDuckGo).
  - ↑ / ↓ choose a result, Enter opens it, Esc closes.
- **Pinned apps:** press the pin next to an app result to keep it as a tile
  (up to 4). Click a tile to launch it; right-click to unpin.
- When nothing is playing, the island opens on this view.
- **Focus:** the island takes keyboard focus only when you click the search
  box or use the shortcut. Esc returns focus to the previous window, and
  clicking elsewhere also ends typing.
- App names and icons are read from `shell:AppsFolder` on a background STA
  thread and cached, so typing never waits on the shell.

## Staying out of the way

- The whole window is click-through (`WS_EX_TRANSPARENT`) unless the pointer is
  on the island. Alerts and the sliver never block the browser tabs underneath.
- It opens only after the pointer stays about 0.2 s on the top-edge zone,
  within 130 px of the centre. Moving down onto a tab leaves the zone first.
- While retracted, a 7 px glass sliver remains only when something is live.
  It shows timer or song progress and the privacy dots.
- It hides completely while a fullscreen app owns the screen.
- It never takes keyboard focus, and it has no taskbar or Alt+Tab entry.

## Liquid glass, cheaply

- `Glass.cs` is an HLSL pixel shader, compiled at startup with the in-box
  `d3dcompiler_47.dll` and run by WPF on the GPU. It masks the backdrop to a
  rounded rectangle smoothly unioned with the bubble (that union is the
  "liquid" merge), then applies blur, lens refraction near the rim, chromatic
  fringing, a specular rim and an adaptive tint.
- The backdrop is a half-resolution GDI capture of the screen behind the
  window. The island excludes itself from capture (`WDA_EXCLUDEFROMCAPTURE`),
  so it also never appears in screenshots or screen shares.
- Captures run only while the island is visible (about 6 per second; every
  5 s for the sliver). A frame is handed to the GPU only if the pixels
  changed, so a static page costs one `memcmp`.
- The render loop runs only while springs or fades are moving. The audio
  spectrum (WASAPI loopback + FFT) runs only while music bars are on screen.

Measured on this machine: idle about 0.3–0.5% of one core, expanded over a
static page about 0%, about 130 MB private memory (mostly WPF).

Without ps_3_0 GPU support it falls back to a solid dark island.

## Diagnostics

- `ISLET_DIAG=1` logs frame, capture and timer counters every 5 s.
- `ISLET_CAPTURABLE=1` keeps the island visible to screenshots, for checking
  the visuals; the glass then refreshes only while hidden.
