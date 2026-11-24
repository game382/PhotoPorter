# PhotoPorter

Copies all photos and videos from an iPhone (USB) to a folder on your PC, such as `D:\iPhone Photos`.

- Scans every folder on the phone (the dated `202301__` style folders included)
- Copies pictures first, then videos, then other files (e.g. `.AAE` edits), oldest folder first
- Overall and current-file progress with time estimates
- Detects stalls/timeouts (60 s with no data), reconnects and retries automatically
- Resumable: skips files already copied (same name and size), unfinished files are never left half-written
- Keeps the PC awake while copying

## Install
Download `PhotoPorter-Setup.msi` from [Releases](../../releases) and run it (no admin needed, Windows 10/11 x64).
Or download the standalone `PhotoPorter.exe` and just run it.

## Before copying (on the iPhone)
1. Unlock and tap **Trust**.
2. Settings → Photos → Transfer to Mac or PC → **Keep Originals**.
3. Turn off Low Power Mode, set Settings → Display & Brightness → Auto-Lock → **Never**, keep it plugged in.

## Build
```
dotnet publish PhotoPorter.csproj -c Release -o publish
dotnet build installer/PhotoPorter.Installer.wixproj -c Release
```
