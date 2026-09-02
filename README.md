# Windows Event Log Viewer

A single-file PowerShell + WinForms GUI for browsing Windows Event Logs, with quick-filter presets for common IT/security investigations, application-name search, and security-identity search (by user, host, or IP).

## Running

```powershell
powershell.exe -File EventLogViewer.ps1
```

Or download the latest `EventLogViewer.exe` from [Releases](../../releases) — no PowerShell console window, just double-click to run. Right-click → "Run as Administrator" if you need to query the Security log (or any preset that reads it).

A few seconds after launch, the app checks GitHub for a newer release in the background and, if one exists, shows a clickable "Update available" link in the status bar — click it to open the release page. This never blocks startup or shows an error if the machine is offline or can't reach GitHub; it just silently skips the check.

## Using the app

### Manual filters (top of the window)

| Field | What it does |
|---|---|
| **Log** | Log to query. Type any log name (e.g. `Application`, `System`, `Security`) — not limited to the dropdown list. |
| **Level** | Any / Critical / Error / Warning / Information / Verbose. |
| **Event ID** | Comma-separated Event IDs, e.g. `7045,7036`. Leave blank for all. |
| **Max Events** | Cap on how many events are pulled back (100–50,000). |
| **Keyword** | Client-side substring filter against the event message — works alongside a manual search, a preset, or either search below. |
| **From / To** | Optional date/time range (check the box to enable). |

Click **Search**, or press Enter in any of the fields above.

### Application search

Enter an application or product name (e.g. `Chrome`, `SQL Server`) and click **Find App Events**. This discovers every event provider whose name matches, pulls from every log those providers write to, and also checks the Application log's generic crash/hang IDs (1000/1001/1002) in case the app's own crash reports were logged under a generic "Application Error" source instead of its own provider name.

### Security identity search

Enter a **User**, **Host**, and/or **IP** (any combination — at least one is required) and click **Search Security Events**. This searches a curated set of identity-relevant Security log events (logon/logoff, explicit-credential logons, special-privilege logons, account management, group membership changes, lockouts, Kerberos) and keeps only the ones whose message text matches every field you filled in. Requires Administrator privileges to read the Security log.

### Quick Filters

One-click presets — see the full reference below. Clicking one sets the Log field and runs the search immediately; the Keyword box, if filled in, still applies on top of it.

### Results grid

- Click a row to see its full message in the detail pane below.
- **Filter results** (above the grid) does a live substring filter across Message, Source, and Event ID without re-querying the log.
- **Export CSV** saves the current result set (Time, Level, Source, Event ID, truncated Message).

## Preset reference

Windows reuses small Event IDs across unrelated providers within the same log (e.g. System-log ID `1` is used by dozens of different sources). Where that's a real risk, presets below are scoped to a specific `ProviderName`, or post-filtered by message text — noted in the "Scoped by" column. Presets without a note filter on Log + Event ID alone.

### System Changes

| Preset | Log(s) | Event ID(s) | What it shows | Scoped by |
|---|---|---|---|---|
| Software Installs | Application | 11707, 1033, 1034 | MsiInstaller product install/remove | |
| Service Changes | System | 7045, 7036 | Any service installed or changed running/stopped state | |
| Driver Installs | System | 7045 | Kernel/file system driver installs specifically | message contains "driver" |
| Startup/Shutdown | System | 6005, 6006, 1074, 6008 | Boot, clean shutdown, user-initiated shutdown, unexpected shutdown | |

### Account/Policy

| Preset | Log(s) | Event ID(s) | What it shows | Scoped by |
|---|---|---|---|---|
| User Acct Changes | Security | 4720, 4722, 4725, 4726, 4738 | Account created, enabled, disabled, deleted, modified | |
| Policy Changes | Security | 4719, 4739 | System/domain audit policy changed | |
| Logon Events | Security | 4624, 4625, 4634, 4647 | Successful/failed logon and logoff | |
| Account Lockouts | Security | 4740 | Account locked out after failed logon attempts | |
| Group Membership Chg | Security | 4728, 4729, 4732, 4733, 4756, 4757 | Members added/removed from global/local/universal security groups | |
| Kerberos Auth | Security | 4768, 4769, 4771, 4776 | TGT/service-ticket requests, pre-auth failures, credential validation | |
| Explicit Credential | Security | 4648 | Logon using explicit credentials (RunAs) — possible lateral movement | |
| Special Privileges | Security | 4672 | Admin-equivalent logon — sensitive privileges assigned | |
| Scheduled Task Chg | Security | 4698–4702 | Scheduled task created, deleted, enabled, disabled, or updated | |
| Audit Log Cleared | Security | 1102 | Security audit log was cleared — investigate immediately | |
| PS Script Block Log | Microsoft-Windows-PowerShell/Operational | 4104 | Logged PowerShell script block text (requires Script Block Logging GPO) | |
| Defender Detections | Microsoft-Windows-Windows Defender/Operational | 1116, 1117 | Malware detected / remediation action taken | |

### App Health

| Preset | Log(s) | Event ID(s) | What it shows | Scoped by |
|---|---|---|---|---|
| App Crashes | Application | 1000, 1002 | Application Error and Application Hang (WER) | |
| App Hangs | Application | 1002, 1001 | Hang detection and Windows Error Reporting follow-up | |

### Resources

| Preset | Log(s) | Event ID(s) | What it shows | Scoped by |
|---|---|---|---|---|
| Resource/Memory | System + Application | 2004, 2019, 2020 (System) / 1530 (Application) | Low memory / pool exhaustion / profile warnings | |
| Disk Errors | System | 7, 11, 153 | Bad block, controller I/O error, disk retry | provider `disk` / `Microsoft-Windows-Disk` |

### Printing

| Preset | Log(s) | Event ID(s) | What it shows | Scoped by |
|---|---|---|---|---|
| Print Jobs | Microsoft-Windows-PrintService/Operational | 307 | Document printed — job, user, printer, pages | |
| Print Errors | Microsoft-Windows-PrintService/Operational | 372, 374, 375 | Spooler errors and failed print jobs | |
| Spooler Events | System | 7031, 7034 | Print Spooler service crash or restart | message contains "Spooler" |

### Networking

| Preset | Log(s) | Event ID(s) | What it shows | Scoped by |
|---|---|---|---|---|
| Network Changes | System | 10000, 10001, 4000, 4001 | NIC connect/disconnect (NDIS) | |
| DHCP Events | System | 1001, 1002, 1003 | DHCP lease obtained, renewed, or lost | |
| DNS Errors | System + Application | 1014 (System) / 4015 (Application) | DNS name resolution failure and DNS server errors | |
| Firewall Changes | Security | 4946, 4947, 4950, 2004 | Firewall rule added, modified, or exception changed | |
| RDP Connections | Microsoft-Windows-TerminalServices-RemoteConnectionManager/Operational | 261, 1149 | RDP session auth and successful connections | |
| VPN / Dial-up | Application | 20227, 20226 | RAS/VPN connection success or failure | |

### Active Directory

Domain Controller only — these pull the entire log (bounded by Max Events), since replication/DFSR/DNS-server-role event IDs vary too much to hardcode reliably. Use the Keyword box or the live results filter to narrow down. On a non-DC machine these correctly show "0 records found" rather than an error, since the log doesn't exist there.

| Preset | Log(s) | What it shows |
|---|---|---|
| AD Replication (All) | Directory Service | All Directory Service log entries — replication/health issues |
| DFS Replication (All) | DFS Replication | All DFSR entries — SYSVOL/DFS replication issues |
| DNS Server (All) | DNS Server | All DNS Server role entries |

### Hardware

| Preset | Log(s) | Event ID(s) | What it shows | Scoped by |
|---|---|---|---|---|
| Hardware Errors (WHEA) | System | 1 | Fatal/corrected hardware errors via WHEA | provider `Microsoft-Windows-WHEA-Logger` / `Microsoft-Windows-Kernel-WHEA` |
| Device Install/Removal | Microsoft-Windows-Kernel-PnP/Configuration | 400, 410, 420, 430 | Device driver install/removal lifecycle — type a device name (e.g. "USB") into the Keyword box to narrow by device type | |
| Unexpected Shutdown | System | 41 | Kernel-Power: system rebooted without a clean shutdown — often power/hardware related | provider `Microsoft-Windows-Kernel-Power` |
| BSOD / Bugcheck | System | 1001 | Windows Stop Error (blue screen) — bugcheck code and parameters | provider `Microsoft-Windows-WER-SystemErrorReporting` |

## Building the standalone .exe

Requires the [`ps2exe`](https://github.com/MScholtes/PS2EXE) module (installed automatically on first run if missing):

```powershell
.\Build-Exe.ps1
```

Produces `dist\EventLogViewer.exe`.

## Releasing a new version

1. Update `$script:AppVersion` near the top of `EventLogViewer.ps1` to match the version you're about to tag (this is what the in-app update check compares against - if you forget, the new release won't recognize itself as up to date, and everyone still on the old version will correctly see the update prompt, but the newly-released version's own check will be one release behind until the next bump catches it up).
2. Commit that change.
3. Push a version tag matching `v*.*.*` — a GitHub Actions workflow builds the exe and attaches it to a new [Release](../../releases) automatically:

```bash
git tag v1.2.0
git push origin v1.2.0
```
