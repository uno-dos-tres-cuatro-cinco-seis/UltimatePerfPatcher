# UltimatePerfPatcher

Unofficial performance patch for **8BitDo Ultimate Software V2** (Windows).
Not affiliated with or endorsed by 8BitDo.

The app freezes for ~1.5–3 s after its window appears. A CPU trace shows the cause:
`LanguageTools.IsChineseSimple()` is called for every UI string (694 call sites) and, until you pick a
language manually, rewrites `user.config` on **every** call. Details and all measurements:
[docs/REPORT.md](docs/REPORT.md).

## Try the free workaround first (no patch needed)

1. Open the app → gear icon → **General** → select your language once (even if it is already the
   right one). This stops the config rewrites – it is the biggest win.
2. Optional: untick **Automatic update** in the same dialog. No update window on every start, and
   device detection keeps working (the app stops detecting newly plugged devices once it has seen an
   update notice).

## What the patcher changes

| Patch | Effect |
|---|---|
| language lookup saves `user.config` once per start | startup freeze ~1.5–3 s → ~0.3 s |
| global low-level keyboard hook only while the app window is active | no keyboard processing while minimized / in the tray |
| device polling 5× less often while the window is hidden or minimized | ~75 % less background CPU |
| three 5000×2813 px images halved (same on-screen size) | ~40 MB less memory per image when shown (DoubleSuper devices) |
| `LARGE_ADDRESS_AWARE` on the 32-bit apphost | 4 GB instead of 2 GB address space |

Measured with an Ultimate 2 Bluetooth controller on V1.19 and V1.35 (3 runs each):

| | original | patched |
|---|---|---|
| UI usable after start | 3.3–4.4 s | 0.9–1.0 s |
| CPU while minimized | 406–594 ms/min | 94–141 ms/min |

## Usage

1. Install/unzip the official app from 8BitDo as usual.
2. Download `UltimatePerfPatcher.exe` from the releases and put it into the app folder
   (next to `8BitDo Ultimate Software V2.exe`), then double-click it.
   Or: `UltimatePerfPatcher.exe "C:\path\to\8BitDo Ultimate Software V2.exe"`
3. Start `8BitDo Ultimate Software V2 (perf).exe` (it must stay in the app folder).

The original EXE is never modified. Requirements: Windows 10/11 (.NET Framework 4.8 is built in).
Tested with app versions V1.19 and V1.35. Unknown versions are refused if the code does not look as expected.

## Please note

- **Use the original app for firmware updates.** The patch does not touch the update code, but a
  failed flash can brick a controller and that path was not tested.
- The patched EXE is not code-signed (the original signature cannot survive a modification), so
  SmartScreen may ask once.
- The built-in auto-updater only updates the original EXE. The `(perf)` EXE stays on the old version, so run the patcher again after updating.
- This repository contains no 8BitDo code or files. The patcher only modifies the copy you installed.

## Building

`build.cmd` (needs the .NET SDK 6+) → `dist\UltimatePerfPatcher.exe`.
`src/AdvancePerf` holds the small helper methods the patched app calls, `src/UltimatePerfPatcher` the
patcher (bundle reader/writer, IL patches via [dnlib](https://github.com/0xd4d/dnlib)).
`measure/` contains the PowerShell scripts used for the measurements.
`UltimatePerfPatcher.exe` embeds dnlib (MIT), see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
