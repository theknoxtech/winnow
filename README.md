# Windows Event Log Viewer

A single-file PowerShell + WinForms GUI for browsing Windows Event Logs, with quick-filter presets for common IT/security investigations, application-name search, and security-identity search (by user, host, or IP).

## Running

```powershell
powershell.exe -File EventLogViewer.ps1
```

Or download the latest `EventLogViewer.exe` from [Releases](../../releases) — no PowerShell console window, just double-click to run. Right-click → "Run as Administrator" if you need to query the Security log.

## Features

- **Quick Filters**: one-click presets for software installs, service/driver changes, account and policy changes, logon events, app crashes, hardware errors, printing, networking, and more.
- **Application search**: enter an app name to pull matching events across every log its provider writes to.
- **Security identity search**: find Security-log events by user, hostname, and/or IP address.
- **Manual filter panel**: log name, level, event ID, keyword, and date range.
- **Live results filter**, CSV export, and a detail pane for full event messages.

## Building the standalone .exe

Requires the [`ps2exe`](https://github.com/MScholtes/PS2EXE) module (installed automatically on first run if missing):

```powershell
.\Build-Exe.ps1
```

Produces `dist\EventLogViewer.exe`.

## Releasing a new version

Push a version tag matching `v*.*.*` and a GitHub Actions workflow builds the exe and attaches it to a new [Release](../../releases) automatically:

```bash
git tag v1.1.0
git push origin v1.1.0
```
