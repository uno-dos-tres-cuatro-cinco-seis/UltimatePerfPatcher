# Performance findings – 8BitDo Ultimate Software V2 (Windows)

Analysed builds: package V1.19 (assembly 1.16.0.0) and package V1.35 (all findings below are
unchanged in V1.35). Runtime: .NET 5.0.17 self-contained single-file, win-x86.
Method: decompiled IL, resource inventory, EventPipe CPU traces, and process-level measurements on
Windows 11 with an Ultimate 2 Bluetooth Controller attached (3 runs per variant).

## 1. Startup freeze of ~2.6–3.3 s caused by `LanguageTools.IsChineseSimple()` (highest impact)

```csharp
public static bool IsChineseSimple() {
    if (Settings1.Default.start == 0) {          // true until the user picks a language manually
        Settings1.Default.Language = getLanguage() == 1 ? 1u : 0u;
        Settings1.Default.Save();                // rewrites user.config on EVERY call
    }
    ...
}
```

`IsChineseSimple()` has 694 call sites (every localized string). `start` only becomes 1 when the
user selects a language in the settings dialog, so for most users each string lookup rewrites
`user.config` on the UI thread. A CPU trace attributes 2,456 ms of the 2,739 ms `MainPage`
constructor to this method (2,440 ms inside `ApplicationSettingsBase.Save()`).

| Measured (controller attached) | original | `Save()` only once per process |
|---|---|---|
| longest UI-thread stall after the window appears | 2,580–2,995 ms | 361–385 ms |
| time until the UI is responsive | 3.4–3.8 s | 0.96–1.02 s |

*Fix:* save only when the value actually changes (or once), or determine the language once at startup.

## 2. Global low-level keyboard hook for the whole app lifetime

`MainWindow..ctor` → `StartReadKey()` → `SetWindowsHookEx(WH_KEYBOARD_LL = 13, …, dwThreadId = 0)`.
Every keystroke system-wide is routed through the app's UI thread, also while it sits minimized or in
the tray. Measured: ~22,000 synthetic key events with the app minimized cost 531 ms of app CPU; with
the hook installed only while the window is active, 0 ms. `KeyboardHookProc` also returns `0`
without calling `CallNextHookEx`.

*Fix:* install the hook only while macro recording / the LED views are active (or while the window
is active), call `CallNextHookEx`, or use WPF `PreviewKeyDown`.

## 3. Device polling every second on two threads, independent of window state

`MainWindow.animation` (`Sleep(1000)` + `FindHid_DLL` + open/close per device) and `Switch.start`
(`Sleep(1000)` + `findSwitchDll`). With the window minimized, backing off 5× while hidden reduced
process CPU from 469 to 94 ms per minute (controller attached).

*Fix:* `RegisterDeviceNotification` / `WM_DEVICECHANGE` instead of polling, or back off while hidden.

## 4. Device detection stops when an update is available

`MainPage.checkToolUpdate` sets `S_Const.updateapp = 1` when the server reports a newer version, and
`MainWindow.animation` loops `while (thread == 0 && S_Const.updateapp == 0)` — the polling thread
ends, so later plug/unplug events are no longer detected until the app is restarted. It also starts
`data\Autoupdate.exe` on every launch while an update is pending. In `Autoupdate.exe`,
`checkUpdate()` calls itself recursively without delay when the response is empty (network error),
which can end in a stack overflow.

## 5. Network requests

`HttpTool.new_CommonHttpRequest` is synchronous `HttpWebRequest` with a 9 s timeout and a new
`Thread` per request; `Post()` ignores its `param` argument and always sends an empty body. During
our runs with a controller attached the server frequently did not answer and ~17 requests per 30 s
timed out.

## 6. Oversized image resources and 2 GB address space

`resource/selectplatform/doublesuper_outline*.png` are 5000×2813 px (≈9 MB each, ≈54 MB each decoded)
although WPF lays them out at 1600×900 DIP (300 dpi metadata). 1,561 PNGs (66 MB; V1.35: 1,631 / 76 MB)
are embedded; the 1,537 `new BitmapImage(...)` call sites never use `Freeze()` or `DecodePixelWidth`.
The apphost is 32-bit **without** `LARGE_ADDRESS_AWARE`.

*Fix:* export images at displayed size ×2, set `/LARGEADDRESSAWARE` on the apphost.

## 7. Eager construction of all pages

`MainPage` constructs 18 pages in field initializers on the UI thread right after the window appears.
After fixing (1) the remaining stall is ~0.37 s on .NET 5; with .NET 8 + ReadyToRun it is below 0.12 s.

## 8. Runtime

.NET 5 has been out of support since May 2022 and the app assembly is not ReadyToRun-compiled. The
unmodified application runs on .NET 8.0.22 (x86, self-contained). Measured with fix (1)–(3) applied:

| | .NET 5 original | .NET 5 + fixes | .NET 8 + fixes + ReadyToRun |
|---|---|---|---|
| longest UI stall after window shown | 2.6–3.0 s | 0.36–0.39 s | 0.003–0.12 s |
| working set (peak) | 158–164 MB | 158–168 MB | 136–138 MB |

## V1.35 measurements

Same patches applied to V1.35 (controller attached, 3 runs each):

| | V1.35 original | V1.35 + fixes (1)–(3) |
|---|---|---|
| longest UI stall after window shown | 1,347–2,097 ms | 232–356 ms |
| time until the UI is responsive | 3.3–4.4 s | 0.91–1.0 s |
| process CPU while minimized | 406 ms/min | 94 ms/min |
| hook CPU, ~20k key events while minimized | 641 ms | 31 ms |

V1.35 (`S_Const.appversion = "135"`) also receives a "new version" answer from the update check
during our tests, so finding (4) applies to it as well.

## Minor

- The V1.35 package's main `8BitDo Ultimate Software V2.exe` is not Authenticode-signed (V1.19's
  was), and the MD5 reported by `/soft/checkversion` (`c3bf153e…`) does not match the downloaded
  ZIP (`cf0f4ab0…`, size identical: 156,113,257 bytes).
- UI-thread HID I/O in `UpdatePage.RefreshView` exists but caused only one ~54 ms stall per device
  change in our tests.
