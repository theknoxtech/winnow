<img src="docs/images/icon.png" align="right" width="104" alt="Winnow icon">

# Winnow

**Cut the chaff out of the Windows Event Log.**

A single-file PowerShell + WinForms app for triaging Windows Event Logs, with quick-filter presets
for common IT and security investigations, application-name search, and security-identity search
by user, host, or IP.

Event Viewer will show you everything. Winnow's job is the opposite: 36 curated presets that go
straight to the events that explain what actually went wrong on the machine in front of you.

Built for remote support: one file, no installer, no prerequisites beyond Windows PowerShell 5.1,
which ships in-box on every supported version of Windows. It is designed to work correctly inside
a **ScreenConnect Backstage** session.

![The main window: filter panel, Quick Filters, results grid and detail pane](docs/images/main-window.png)

## Running

Download **`Winnow.exe`** from [Releases](../../releases) and run it. That is the whole install —
copy the single file wherever you need it. Right-click → *Run as Administrator* if you need the
Security log, or any preset that reads it.

Every release also ships **`Winnow.ps1`** — the same application as a plain script. It is there as
a fallback, for the reason below.

### Two files, and which one to use

`Winnow.exe` is the one to reach for. `Winnow.ps1` matters when it gets blocked.

Both are unsigned, and Defender has quarantined unsigned builds from this project before — v1.3.0
as `Trojan:Win32/Wacatac.B!ml` and v1.3.1 as `Trojan:Win32/Wacatac.C!ml`. Those are cloud
machine-learning verdicts, not signature matches: they react to an executable being unsigned,
newly built and rare, largely regardless of what the code does. Signing is the real fix and is not
available to this project (see [Code signing](#code-signing)).

That classifier applies to **PE binaries**. A `.ps1` is a text script, so it cannot apply to one at
all. If the exe is ever blocked on a machine, the script runs the same application:

```powershell
powershell.exe -STA -ExecutionPolicy Bypass -File .\Winnow.ps1
```

- **`-ExecutionPolicy Bypass`** — a script downloaded through a browser carries a Mark-of-the-Web
  tag, and under the common `RemoteSigned` policy Windows refuses to run it (*"not digitally
  signed. You cannot run this script"*). Bypass applies to that one invocation and changes no
  machine-wide setting. `Unblock-File .\Winnow.ps1` once is the alternative.
- **`-STA`** — WinForms needs a single-threaded apartment.

Both files are unsigned, so each release publishes a SHA-256 for each — worth
[checking](#verifying-a-download) before you put either on a customer machine.

### In ScreenConnect Backstage

Copy the file to the machine and run it from the Backstage command prompt:

```bat
C:\Windows\Temp\Winnow.exe
```

Or, using the script:

```bat
powershell.exe -STA -ExecutionPolicy Bypass -File C:\Windows\Temp\Winnow.ps1
```

Backstage runs processes as `NT AUTHORITY\SYSTEM` on a separate desktop, which changes a few
things. The script detects that and adapts:

| | Behaviour in Backstage |
|---|---|
| **Security log** | Works with no elevation prompt — the process is already SYSTEM. |
| **CSV export** | Skips the file dialog, which is unreliable on an alternate desktop, and writes to `%TEMP%\Winnow\` instead, copying the path to the clipboard. Retrieve it with ScreenConnect file transfer. |
| **Update link** | Copies the release URL rather than launching a browser as SYSTEM. |
| **Window size** | Sized from the actual desktop, so it fits a 1024×768 Backstage screen instead of opening partly off it. |
| **Preset strip** | Scrolls within a fixed height, so 36 buttons cannot push the results grid off a short screen. |
| **Presets…** | Works unchanged. The editor is an ordinary window in the same process, so it needs no shell dialog and no external editor - both of which are unreliable here. |

The status bar shows which mode it detected, e.g. `SYSTEM - Winsta0\Backstage desktop`, so you can
tell at a glance which behaviour is in effect.

## Using the app

### Manual filters

| Field | What it does |
|---|---|
| **Log** | Log to query. Editable — type any log name, not just the listed ones. |
| **Level** | Any / Critical / Error / Warning / Information / Verbose. |
| **Event ID** | Comma-separated Event IDs, e.g. `7045,7036`. Blank for all. |
| **Max Events** | Cap on how many events are pulled back, 100–50,000. |
| **Keyword** | Substring filter against the event message. Applies on top of a preset or either search below. |
| **From / To** | Optional date range (tick the box to enable). |

Press **Search**, or Enter from any of the filter fields.

The window stops repainting while a large search runs — the query is synchronous, and there is no
cancel. On a busy server, a 50,000-event search can take a while; prefer a tighter Max Events or a
date range over waiting it out.

### Quick Filters

One-click presets, colour-coded by group — see the [reference](#preset-reference) below. Clicking
one sets the Log field and runs immediately; the Keyword box, if filled in, still applies on top.

![The Quick Filters strip, with presets colour-coded by group](docs/images/quick-filters.png)

### Application search

Enter an application or product name (e.g. `Chrome`, `SQL Server`) and click **Find App Events**.
This finds every event provider whose name matches, queries every log those providers write to,
and also checks the Application log's generic crash/hang IDs (1000/1001/1002) in case the app's
crashes were logged under "Application Error" rather than its own provider name.

### Security identity search

Enter a **User**, **Host**, and/or **IP** — any combination, at least one — and click **Search
Security Events**. Searches a curated set of identity-relevant Security events (logon/logoff,
explicit-credential and special-privilege logons, account management, group membership changes,
lockouts, Kerberos) and keeps only rows matching every field you filled in. Needs Administrator,
or Backstage.

### Results

- Click a row to see its full message in the detail pane.
- **Filter results** narrows the rows already loaded, across Message, Source and Event ID, without
  re-querying.
- **Export CSV** writes the full result set of the last search. Note that it exports everything
  the search returned, not the narrowed view — the **Filter results** box changes what you see,
  not what gets exported.

## Presets

The 36 built-in presets are baked into the app, so with `Winnow.exe` they cannot be edited in
place. A **`presets.json` file placed next to the executable** is what makes them editable without
a rebuild — an external file is readable whatever is compiled in, so this works the same for the
exe and the script.

Click **Presets…** in the toolbar. The file is found next to `Winnow.exe` (or `Winnow.ps1`)
wherever you put it, regardless of the working directory you launched from.

### The preset editor

The **Presets…** window lists every preset with its status — `Built-in`, `Modified`, `Custom`, or
`Hidden` — and edits them in place. Changes apply as soon as you save; there is no restart.

| Button | What it does |
|---|---|
| **New** | Adds a custom preset. |
| **Clone** | Copies the selected preset, including a built-in, as a starting point. |
| **Delete** | Removes a custom preset. Built-ins are hidden rather than deleted, using **Hide this preset**, so they can always be brought back. |
| **Test** | Runs the preset against this machine and reports how many events it matched, with the timestamp of the most recent. The quickest way to catch a wrong Event ID. |
| **Save** | Writes `presets.json` and reloads the preset strip. |
| **Cancel** | Discards everything. The editor works on a copy, so nothing is written until you save. |

Only differences from the built-ins are saved. A preset you never touched is absent from the file
entirely, which means it keeps tracking future Winnow releases rather than being pinned to
whatever its definition happened to be the day you opened the editor.

### presets.json

The editor is a front end to this file, not a replacement format — hand-editing works exactly as
before, and the editor will read whatever you write. Saving from the editor rewrites the file:
your changes survive, your formatting and any comments do not.

Three things are possible. A `label` that matches a built-in **changes** it, listing only the
fields you want different. An unrecognised `label` **adds** a preset. `"disabled": true`
**removes** one.

```json
{
  "presets": [
    { "label": "Logon Events", "id": [4624, 4625, 4634, 4647, 4648] },

    { "label": "Print Jobs", "disabled": true },

    {
      "group": "Custom",
      "label": "LOB App Errors",
      "logName": "Application",
      "id": [4001, 4002],
      "providerName": ["AcmeLOB"],
      "description": "Errors from our line-of-business application"
    }
  ]
}
```

| Field | Meaning |
|---|---|
| `label` | Button text, and the key used to match a built-in. Required. |
| `group` | Controls the button's colour and grouping. Defaults to `Custom`. A new group falls back to the default button colour. |
| `description` | The button's tooltip. |
| `logName` | Log to query. Required on a new preset. |
| `id` | Event IDs. Omit for every event in the log — this is how the Active Directory presets work. |
| `logName2` / `id2` | An optional second log, queried and merged with the first. |
| `providerName` | Restricts to specific sources. Important when an Event ID is reused. |
| `messageFilter` | Substring applied to the message after the query runs. |
| `disabled` | `true` hides a built-in preset. |

Field names are case-insensitive. A malformed file never takes the app down — it falls back to the
built-in presets and explains why in the status bar, because a typo in your overrides should cost
you the overrides, not the tool.

To change the built-ins themselves rather than override them, edit the `$script:Presets` array
near the top of `Winnow.ps1`; the same field names apply in PascalCase (`LogName`, `Id`).

**Why `ProviderName` matters:** Windows reuses small Event IDs across unrelated providers in the
same log — System-log ID `1` is used by dozens of sources. Without a provider scope, a preset on
ID 1 returns a flood of unrelated events.

**Why `MessageFilter` exists:** some Event IDs are shared with no distinguishing provider at all.
Service Control Manager's 7031/7034 are emitted for *every* service on the machine, and only the
message text says which one, so the Spooler preset filters on the text.

After editing presets, regenerate the reference table below so the docs cannot drift:

```powershell
.\build\Generate-PresetDocs.ps1
```

## Preset reference

Windows reuses small Event IDs across unrelated providers within the same log. Where that is a
real risk, presets below are scoped to a specific provider or post-filtered by message text —
shown in the "Scoped by" column. Presets without a note filter on log and Event ID alone.

<!-- PRESET-REFERENCE:START - generated by build\Generate-PresetDocs.ps1, do not edit by hand -->

### Account/Policy

| Preset | Log(s) | Event ID(s) | What it shows | Scoped by |
|---|---|---|---|---|
| User Acct Changes | Security | 4720, 4722, 4725, 4726, 4738 | Account created, enabled, disabled, deleted, modified |  |
| Policy Changes | Security | 4719, 4739 | System and domain audit policy changed |  |
| Logon Events | Security | 4624, 4625, 4634, 4647 | Successful/failed logon and logoff |  |
| Account Lockouts | Security | 4740 | User account locked out after failed logon attempts |  |
| Group Membership Chg | Security | 4728, 4729, 4732, 4733, 4756, 4757 | Members added/removed: global, local, universal security groups |  |
| Kerberos Auth | Security | 4768, 4769, 4771, 4776 | TGT/service-ticket requests, pre-auth failures, credential validation |  |
| Explicit Credential | Security | 4648 | Logon using explicit credentials (RunAs) - possible lateral movement |  |
| Special Privileges | Security | 4672 | Admin-equivalent logon - sensitive privileges assigned |  |
| Scheduled Task Chg | Security | 4698, 4699, 4700, 4701, 4702 | Scheduled task created, deleted, enabled, disabled, or updated |  |
| Audit Log Cleared | Security | 1102 | Security audit log was cleared - investigate immediately |  |
| PS Script Block Log | Microsoft-Windows-PowerShell/Operational | 4104 | Logged PowerShell script block text (requires Script Block Logging GPO) |  |
| Defender Detections | Microsoft-Windows-Windows Defender/Operational | 1116, 1117 | Malware detected / remediation action taken |  |

### Active Directory

| Preset | Log(s) | Event ID(s) | What it shows | Scoped by |
|---|---|---|---|---|
| AD Replication (All) | Directory Service | _all_ | All Directory Service log entries - replication/health issues (Domain Controllers only) |  |
| DFS Replication (All) | DFS Replication | _all_ | All DFSR entries - SYSVOL/DFS replication issues (Domain Controllers only) |  |
| DNS Server (All) | DNS Server | _all_ | All DNS Server role entries (Domain Controllers/DNS role only) |  |

### App Health

| Preset | Log(s) | Event ID(s) | What it shows | Scoped by |
|---|---|---|---|---|
| App Crashes | Application | 1000, 1002 | Application Error and Application Hang (WER) |  |
| App Hangs | Application | 1002, 1001 | Hang detection and Windows Error Reporting follow-up |  |

### Hardware

| Preset | Log(s) | Event ID(s) | What it shows | Scoped by |
|---|---|---|---|---|
| Hardware Errors (WHEA) | System | 1 | Fatal/corrected hardware errors via WHEA - ID 1 alone is one of the most-reused IDs in the System log, so this is scoped to the WHEA providers specifically | provider `Microsoft-Windows-WHEA-Logger`, `Microsoft-Windows-Kernel-WHEA` |
| Device Install/Removal | Microsoft-Windows-Kernel-PnP/Configuration | 400, 410, 420, 430 | Device driver install/removal lifecycle - tip: use Keyword box to filter by device type (e.g. "USB") |  |
| Unexpected Shutdown | System | 41 | Kernel-Power: system rebooted without a clean shutdown - often power/hardware related | provider `Microsoft-Windows-Kernel-Power` |
| BSOD / Bugcheck | System | 1001 | Windows Stop Error (blue screen) - bugcheck code and parameters | provider `Microsoft-Windows-WER-SystemErrorReporting` |

### Networking

| Preset | Log(s) | Event ID(s) | What it shows | Scoped by |
|---|---|---|---|---|
| Network Changes | System | 10000, 10001, 4000, 4001 | NIC connect/disconnect (NDIS) |  |
| DHCP Events | System | 1001, 1002, 1003 | DHCP lease obtained, renewed, or lost |  |
| DNS Errors | System<br>Application | 1014<br>4015 | DNS name resolution failure and DNS server errors |  |
| Firewall Changes | Security | 4946, 4947, 4950, 2004 | Firewall rule added, modified, or exception changed |  |
| RDP Connections | Microsoft-Windows-TerminalServices-RemoteConnectionManager/Operational | 261, 1149 | RDP session auth and successful connections |  |
| VPN / Dial-up | Application | 20227, 20226 | RAS/VPN connection success or failure |  |

### Printing

| Preset | Log(s) | Event ID(s) | What it shows | Scoped by |
|---|---|---|---|---|
| Print Jobs | Microsoft-Windows-PrintService/Operational | 307 | Document printed — job, user, printer, pages |  |
| Print Errors | Microsoft-Windows-PrintService/Operational | 372, 374, 375 | Spooler errors and failed print jobs |  |
| Spooler Events | System | 7031, 7034 | Print Spooler service crash or restart (IDs 7031/7034 are generic Service Control Manager events shared by every service, filtered here to Spooler by message text) | message contains "Spooler" |

### Resources

| Preset | Log(s) | Event ID(s) | What it shows | Scoped by |
|---|---|---|---|---|
| Resource/Memory | System<br>Application | 2004, 2019, 2020<br>1530 | Low memory / pool exhaustion / profile warnings |  |
| Disk Errors | System | 7, 11, 153 | Bad block, device I/O error, disk reset - IDs 7/11/153 are also reused by unrelated providers (e.g. Hyper-V networking, Kernel-Boot), so this is scoped to the disk drivers specifically | provider `disk`, `Microsoft-Windows-Disk` |

### System Changes

| Preset | Log(s) | Event ID(s) | What it shows | Scoped by |
|---|---|---|---|---|
| Software Installs | Application | 11707, 1033, 1034 | MsiInstaller product install/remove |  |
| Service Changes | System | 7045, 7036 | New service installed or state changed |  |
| Driver Installs | System | 7045 | Kernel/file system driver installed (ID 7045 is shared by every new service; filtered here to entries whose Service Type mentions "driver") | message contains "driver" |
| Startup/Shutdown | System | 6005, 6006, 1074, 6008 | Boot, clean shutdown, unexpected shutdown, restart reason |  |

<!-- PRESET-REFERENCE:END -->

Active Directory presets pull the entire log (bounded by Max Events), because replication, DFSR
and DNS-server event IDs vary too much to hardcode. On a machine without those logs they show
"0 records" and say so, rather than erroring.

## Verifying a download

Every release publishes a SHA-256 for both files, in the release notes and as `.sha256` assets.
Neither is signed, so this is what confirms you have the file that was actually published:

```powershell
(Get-FileHash Winnow.exe -Algorithm SHA256).Hash
(Get-FileHash Winnow.ps1 -Algorithm SHA256).Hash
```

`Winnow.ps1` has one advantage the exe does not: you can simply read it. It is a single
self-contained script with no obfuscation and no downloaded payloads — everything it does is on
the page. The exe is that same script compiled with
[ps2exe](https://github.com/MScholtes/PS2EXE), which embeds it and hosts the PowerShell runtime,
so the two are the same application by construction rather than by promise.

## Code signing

Winnow is **unsigned**, and there is no signing in the release pipeline.

Signing certificates are a recurring cost, and since the June 2023 CA/Browser Forum baseline the
private key must live on FIPS 140-2 Level 2 hardware, so there is no cheap path — a downloadable
`.pfx` is not an option. Azure Artifact Signing's individual tier requires personal identity
verification (government ID and a live check). An application to
[SignPath Foundation](https://signpath.org/), whose free open-source program vets the project
rather than the maintainer, was not approved.

Shipping a script rather than a binary is what makes this tolerable: the PE classifier that
quarantined the compiled builds does not apply to a `.ps1`, and a reader can audit the source
directly instead of trusting an opaque executable. What is genuinely lost is that a customer's
security team cannot write an AppLocker or WDAC *publisher* rule for unsigned code — they need a
hash rule, which is why every release publishes its SHA-256.

[`PRIVACY.md`](PRIVACY.md) covers what the script does with data, how releases are produced, and
who authorises one.

## Updates

A few seconds after launch the script checks GitHub for a newer release and, if there is one,
shows a link in the status bar. It never downloads or installs anything — updating is deliberately
your decision.

The check fails silently. Offline machines, outbound-blocked networks and GitHub rate limits are
all normal in this app's environment, and none of them are worth interrupting an investigation. It
uses the system proxy, since a SYSTEM process has no per-user proxy configuration.

## Development

| Path | Contents |
|---|---|
| `Winnow.ps1` | The application, and the source of truth. Everything else is generated from it. |
| `build/Build-Exe.ps1` | Compiles `Winnow.ps1` into `dist\Winnow.exe` with ps2exe. Refuses to build a script that does not parse, so a typo cannot become a broken exe. |
| `tests/Test-PresetOverrides.ps1` | Tests the `presets.json` merge - partial overlay, disable, add, and safe fallback on a malformed file. Run by CI. |
| `tests/Test-PresetEditor.ps1` | Tests the editor's save path: the delta it writes has to re-import to exactly what it was exported from, or an edit silently reverts on the next launch. Run by CI. |
| `tests/Test-UiSmoke.ps1` | Builds the real window and drives its event handlers, looking for the errors that only appear at runtime - a handler that parses fine and throws the first time it is used. Needs STA, so it re-launches itself under `powershell.exe -STA`. Run by CI. |
| `build/Generate-PresetDocs.ps1` | Regenerates the preset reference above, reading the preset array out of `Winnow.ps1` via the PowerShell parser so the two cannot disagree. |
| `build/Generate-Icon.ps1` | Regenerates `winnow.ico` and `docs/images/icon.png`. |
| `src/`, `tests/` | A C# / WPF rewrite, retained but **not shipped**. Its remaining advantage over the script is cancellable searches; the preset editor and `presets.json` side-car it was built for both exist here now. Kept in case signing ever becomes available and it is worth picking back up. |

Building the exe locally:

```powershell
.\build\Build-Exe.ps1 -Version 2.1.1
```

### Releasing

Push a version tag matching `v*.*.*`. GitHub Actions parses the script, checks its version against
the tag, builds the exe, and publishes both files with their SHA-256s and generated release notes.

```bash
git tag v2.1.1
git push origin v2.1.1
```

`$script:AppVersion` near the top of `Winnow.ps1` must match the tag — the release fails if it does
not, since the in-app update check compares that constant against the latest release.
