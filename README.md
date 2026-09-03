<img src="docs/images/icon.png" align="right" width="104" alt="Winnow icon">

# Winnow

**Cut the chaff out of the Windows Event Log.**

A single-file Windows desktop app for triaging Windows Event Logs, with quick-filter presets for
common IT and security investigations, application-name search, and security-identity search by
user, host, or IP.

Event Viewer will show you everything. Winnow's job is the opposite: 36 curated presets that go
straight to the events that explain what actually went wrong on the machine in front of you.

Built for remote support: one `Winnow.exe`, under a megabyte, no installer and no prerequisites. It
targets .NET Framework 4.8, which ships in-box on Windows 10 1903+, Windows 11, and Server 2019+,
and it is designed to work correctly inside a **ScreenConnect Backstage** session.

![The main window: filter panel, Quick Filters, results grid and detail pane](docs/images/main-window.png)

## Running

Download `Winnow.exe` from [Releases](../../releases) and run it. That is the whole
install — copy the single file wherever you need it.

Right-click → *Run as Administrator* if you need the Security log (or any preset that reads it).

The binary is unsigned, so each release publishes its SHA-256 — worth
[checking](#verifying-a-download) before you put it on a customer machine. Downloading it
through a browser can also trip Windows Defender's cloud classifier — see
[If Windows Defender flags Winnow.exe](#if-windows-defender-flags-winnowexe) if that happens.

This project uses **[SignPath Foundation](https://signpath.org/)** for free code signing of open
source projects. An application is in progress — releases are not signed yet, so the note above
still applies until that changes. See [`SIGNING-POLICY.md`](SIGNING-POLICY.md) for the full policy
this signing will operate under, including the privacy statement.

### In ScreenConnect Backstage

Copy the exe to the machine and run it from the Backstage command prompt:

```bat
C:\Windows\Temp\Winnow.exe
```

Backstage runs processes as `NT AUTHORITY\SYSTEM` on a separate desktop, which changes a few
things. The app detects this and adapts:

| | Behaviour in Backstage |
|---|---|
| **Security log** | Works with no elevation prompt — the process is already SYSTEM. |
| **CSV export** | Skips the file dialog (unreliable on an alternate desktop) and writes to `%TEMP%\Winnow\`, then copies the path to the clipboard. Retrieve it with ScreenConnect file transfer. |
| **Update link** | Copies the release URL instead of launching a browser as SYSTEM. |
| **Window size** | Sized from the actual desktop, so it fits a 1024×768 Backstage screen. |

The status bar shows which mode the app detected, e.g. `SYSTEM · Backstage desktop`, so you can
tell at a glance which behaviour is in effect.

### Command line

```
Winnow.exe [--presets <path>] [--trace-bindings]

  --presets <path>   Load preset overrides from the given presets.json.
                     Defaults to presets.json beside the executable, if present.
  --trace-bindings   Write WPF data-binding warnings to bindings.log. Diagnostics only.
  --help             Show usage.
```

## Using the app

### Manual filters

| Field | What it does |
|---|---|
| **Log** | Log to query. Editable — type any log name, not just the listed ones. |
| **Level** | Any / Critical / Error / Warning / Information / Verbose. |
| **Event ID** | Comma-separated Event IDs, e.g. `7045,7036`. Blank for all. |
| **Max Events** | Cap on how many events are pulled back. |
| **Keyword** | Substring filter against the event message. Applies on top of a preset or either search below. |
| **From / To** | Optional date range (tick the box to enable). |

Press **Search**, or Enter, or F5. **Cancel** (or Escape) stops a running query — useful when a
50,000-event search on a busy server is taking longer than expected.

### Quick Filters

One-click presets, colour-coded by group — see the reference below. Clicking one sets the Log
field and runs immediately; the Keyword box, if filled in, still applies on top.

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
- **Export CSV** writes what the filter currently shows.

## Presets

Presets are data, not code. The 36 built-in presets are embedded in the exe; an optional
`presets.json` beside it (or at `--presets <path>`) adds to, changes, or hides them.

You can edit them two ways: in the app with **Edit Presets…**, or by hand in `presets.json`.
Both write the same file.

![The Quick Filters strip, with presets colour-coded by group](docs/images/quick-filters.png)

### Editing presets in the app

Click **Edit Presets…** in the toolbar.

![The preset editor, with the preset list on the left and the selected preset's fields on the right](docs/images/preset-editor.png)

The list on the left shows every preset, including ones currently turned off, each labelled
`built-in` or `custom`. Selecting one opens it on the right.

**1. Edit the fields.** Label and group drive the button; group also picks its colour, and a group
you invent gets a colour of its own. Description becomes the button's tooltip — worth filling in
if the preset's Event IDs are shared with unrelated sources, so the next person knows why it is
scoped the way it is.

**2. Edit the clauses.** Each clause queries one log, with its own Event IDs and providers.

![A clause: log name, Event IDs and providers](docs/images/preset-editor-clauses.png)

Leave **Event IDs** blank to match every event in the log, which is how the Active Directory
presets work. Use **Providers** when an Event ID is reused across unrelated sources — see the two
notes below.

**Add Clause** gives a preset a second log. Results from every clause are merged and sorted
together by time, which is how a preset spans more than one log — `Resource/Memory` and
`DNS Errors` both do. The [JSON example below](#presetsjson-format) shows the two-clause shape.

**3. Test it.** The **Test** button runs the preset exactly as edited and reports how many events
it matches on this machine. It is the fastest way to catch a wrong Event ID, and it will tell you
if the log does not exist here at all rather than just returning nothing.

![The Test button reporting how many events the edited preset matches](docs/images/preset-editor-test.png)

**4. Save.** Writes `presets.json` beside the exe. If that location is not writable — running from
a temp copy or a read-only share — you are asked where to put it instead.

Only the differences are written, so the file stays small and reviewable in git, and a later fix
to a built-in preset still reaches you.

#### Adding your own

**Add** creates a new preset, ready to fill in; **Clone** copies the selected one, which is usually
the faster start. Either way it lands in the `Custom` group by default, and once saved it appears
in the Quick Filters strip alongside the built-ins with a colour of its own.

![A newly added preset in the editor, ready to be filled in](docs/images/preset-custom.png)

#### Turning a built-in off

**Delete** on a built-in turns it off rather than removing it — the definition is kept, so you can
re-enable it with the **Enabled** box, and a future fix to that preset is not permanently
discarded. Delete on a custom preset removes it outright.

#### Sharing a preset set across the team

**Export…** writes the current set to a file; **Import…** merges one in. Keep the exported
`presets.json` in your IT repo and drop it beside the exe when you push the tool to a machine —
that is the whole mechanism for a shared team preset set, with no server component involved.

### presets.json format

A preset file only needs the presets it changes — everything else keeps the built-in definition,
so a later fix to a built-in still reaches you.

```json
{
  "presets": [
    {
      "id": "custom.our-line-of-business-app",
      "group": "Custom",
      "label": "LOB App Errors",
      "description": "Errors from our line-of-business application",
      "clauses": [
        { "logName": "Application", "eventIds": [4001, 4002], "providerNames": ["AcmeLOB"] }
      ]
    },
    { "id": "printing.print-jobs", "disabled": true }
  ]
}
```

| Field | Meaning |
|---|---|
| `id` | Stable identity. Matching a built-in `id` **replaces** it; a new `id` **adds** a preset. Do not change an id once it is in use. |
| `group` | Controls the button colour and grouping. A new group gets its own colour automatically. |
| `label` | Button text. |
| `description` | Button tooltip. |
| `disabled` | `true` hides a built-in preset. The rest of its definition can be omitted. |
| `clauses` | One or more logs to query. Results are merged and sorted by time. |
| `clauses[].logName` | Log to query. Required. |
| `clauses[].eventIds` | Event IDs. Omit or leave empty for every event in the log. |
| `clauses[].providerNames` | Restricts to specific sources. Important when an Event ID is reused. |
| `messageFilter` | Substring applied to the message after the query runs. |

**Why `providerNames` matters:** Windows reuses small Event IDs across unrelated providers in the
same log — System-log ID `1` is used by dozens of sources. Without a provider scope, a preset on
ID 1 returns a flood of unrelated events.

**Why `messageFilter` exists:** some Event IDs are shared with no distinguishing provider at all.
Service Control Manager's 7031/7034 are emitted for *every* service on the machine, and only the
message text says which one, so the Spooler preset filters on the text.

> Note: a provider name containing an apostrophe cannot be expressed in an event log query — the
> log implements a subset of XPath with no `concat()`, and XPath has no escape inside a string
> literal. Such a name is applied as a post-query filter instead, which still gives correct
> results but reads more of the log to do it.

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
| Print Jobs | Microsoft-Windows-PrintService/Operational | 307 | Document printed - job, user, printer, pages |  |
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

## Building

Requires the [.NET SDK](https://dotnet.microsoft.com/download) (8.0 or later). Visual Studio is
not needed — the projects target .NET Framework 4.8 but build with the SDK alone.

```powershell
.\build\publish.ps1
```

Runs the tests and produces a single self-contained `dist\Winnow.exe`. Dependencies are
merged in with [ILRepack](https://github.com/gluck/il-repack), so the exe has no loose DLLs beside it.

```powershell
dotnet test                             # tests only
.\build\Generate-PresetDocs.ps1         # regenerate the preset reference above
.\build\Generate-Icon.ps1               # regenerate winnow.ico
```

The icon is generated from a script rather than committed as an opaque binary, so it can be
re-coloured or reshaped later without anyone reverse-engineering it in an image editor.

### Layout

| Path | Contents |
|---|---|
| `src/Winnow.Core` | Query engine, preset model, CSV export, update check, host detection. No UI dependency. |
| `src/Winnow.App` | WPF views and view models. |
| `tests/Winnow.Tests` | Unit tests, plus tests that query this machine's real event log. |
| `legacy/` | The original PowerShell + WinForms version, kept for one release cycle. |

## Releasing

Push a version tag matching `v*.*.*`. GitHub Actions runs the tests, builds the exe, verifies the
stamped version matches the tag, and attaches it to a new Release.

```bash
git tag v1.3.0
git push origin v1.3.0
```

There is no version constant to update — the version comes from the tag and is read back out of
the assembly at runtime. (The PowerShell version required updating `$script:AppVersion` by hand to
match the tag, and the release would misreport its own version if you forgot.)

### Code signing, and why there isn't any

Winnow is **unsigned, by choice**. Code signing certificates are a recurring cost, and since the
June 2023 CA/Browser Forum baseline the private key must live on FIPS 140-2 Level 2 hardware, so
there is no longer a cheap path — a downloadable `.pfx` is not an option, leaving a cloud signing
subscription or a hardware token plus a self-hosted build runner.

What that does and does not cost you:

- **SmartScreen is largely moot** for how this tool is meant to be distributed. The "Windows
  protected your PC" prompt is driven by Mark-of-the-Web, which browsers and mail clients attach
  to downloads. A file pushed over a ScreenConnect file transfer, or copied from a UNC path,
  usually carries no MOTW and never triggers it. **Downloading the exe from Releases in a browser
  is a different story** — see below.
- **Application control is the real, unconditional loss.** A customer's security team cannot
  write an AppLocker or WDAC *publisher* rule against an unsigned file. They need a hash rule,
  which is why every release publishes its SHA-256 (below).
- **Defender's cloud ML classifier flags unsigned, low-prevalence binaries — repeatedly, and this
  is not solved.** v1.3.0 was quarantined as `Trojan:Win32/Wacatac.B!ml`. The cause looked
  packaging-specific: it used [Costura](https://github.com/Fody/Costura), which embeds each
  dependency as a compressed resource and loads it from memory at run time via `AssemblyResolve` —
  behaviourally what a packer does. Switching to [ILRepack](https://github.com/gluck/il-repack),
  which merges the IL at build time into one ordinary assembly with no compressed payloads and no
  run-time loading, tested clean on the build machine. **v1.3.1 — the ILRepack build — was then
  independently quarantined on a different machine as `Trojan:Win32/Wacatac.C!ml`**, confirmed by
  cross-referencing the flagged file's CDN blob GUID against the actual release asset, not a stale
  v1.3.0 link.

  The honest reading: removing the Costura pattern was a real improvement and worth keeping — it
  is less packer-like by any static measure, and every download it caught during testing here
  came back clean — but it was not sufficient on its own. `Wacatac.*!ml` is a cloud model verdict
  reacting to the whole profile of the file: unsigned, freshly built, near-zero prevalence, and
  performing operations (`EventLogReader` against the Security log, outbound HTTPS for the update
  check) that a heuristic has no way to distinguish from reconnaissance or exfiltration short of
  vouching for the publisher. Signing is what supplies that vouching; nothing about how the
  assemblies are merged substitutes for it. **This should be expected to recur on future unsigned
  releases**, and a "scanned clean here" claim from any one machine does not predict the verdict
  on another — the same v1.3.1 hash was clean on the machine that built it and flagged on the one
  that downloaded it, which is the cloud model's non-determinism showing directly, not a fluke.

**Signing is wired into the release workflow but not active.** `.github/workflows/release.yml` will
Authenticode-sign `dist\Winnow.exe` via [Azure Artifact
Signing](https://azure.microsoft.com/en-us/products/artifact-signing) (formerly Trusted Signing)
the moment the repository has the following configured — nothing else about the workflow needs to
change:

- **Secrets:** `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` — from an Entra ID app
  registration with a GitHub Actions federated credential (OIDC, no stored password).
- **Variables:** `ARTIFACT_SIGNING_ENDPOINT`, `ARTIFACT_SIGNING_ACCOUNT`, `ARTIFACT_SIGNING_PROFILE`
  — matching an Azure Artifact Signing account with a completed identity validation and a Public
  Trust certificate profile, with that app registration granted the **Artifact Signing Certificate
  Profile Signer** role on it.

Until all of those exist, every release ships exactly as described above — the workflow checks for
`ARTIFACT_SIGNING_ACCOUNT` and skips signing entirely if it's unset. Once they do, the workflow
signs the exe, verifies the result is a `Valid` Authenticode signature before publishing anything
(failing the build otherwise, rather than shipping something quietly broken), and the release notes
carry a short signed-release note in place of the Defender warning above.

**This path requires personal identity verification for an individual** (government ID, a live
check) and is not currently being pursued for that reason — the wiring above stays in place,
dormant and harmless, in case that changes later.

**The path actually being pursued is [SignPath Foundation](https://signpath.org/)'s free signing
for open-source projects** — no personal ID; the verification is of the project and its source,
not the maintainer. Winnow meets its stated eligibility criteria (OSI license, already released,
actively maintained, public repository). [`SIGNING-POLICY.md`](SIGNING-POLICY.md) is the policy
page their program requires — team roles, build provenance, and a privacy statement grounded in an
actual audit of the codebase's one network call. **The application has not been submitted yet** —
that page exists ahead of it, since a ready policy page is itself part of showing the project is
prepared. CI integration for SignPath doesn't exist yet either; that's deliberately deferred until
after acceptance, to be built against their actual current submission mechanism rather than
guessed at now, the same way the Azure integration above was verified against Microsoft's current
docs rather than assumed.

### If Windows Defender flags Winnow.exe

You may see `Trojan:Win32/Wacatac.B!ml` or `Trojan:Win32/Wacatac.C!ml`, marked Severe, quarantined
automatically. The `!ml` means this is a cloud machine-learning verdict, not a signature match
against known malware — it is reacting to the file being unsigned and unfamiliar, not to anything
specific the code does. See [above](#code-signing-and-why-there-isnt-any) for why, and why it is
not currently fixable without a paid certificate.

What to do about it:

1. **Verify the file first, always.** Check its SHA-256 against the value published with the
   [release](../../releases) — see [Verifying a download](#verifying-a-download) — before assuming
   it's a false positive rather than a compromised build.
2. **Prefer pushing the file over ScreenConnect, not a browser download from Releases**, where
   possible. Both incidents so far were logged with `Detection Source: Downloads and attachments`
   — the path this tool's primary Backstage use case does not go through.
3. **If it's already been quarantined**, Windows Security → Protection history → find the entry →
   Actions → *Restore* brings the file back once you've confirmed the hash.
4. **Consider submitting the flagged release to Microsoft** at
   [www.microsoft.com/wdsi/filesubmission](https://www.microsoft.com/en-us/wdsi/filesubmission) —
   submitter type *Software developer*, "I believe this file should not be detected as malware",
   detection name as shown. This is worth doing for whichever release is *currently* live; it does
   nothing for a superseded one.

### Verifying a download

Every release publishes the SHA-256 of `Winnow.exe`, both in the release notes and as a
`Winnow.exe.sha256` asset. Since the binary is unsigned, this is what stands in for a publisher
signature — check it before running the file on a customer machine:

```powershell
(Get-FileHash Winnow.exe -Algorithm SHA256).Hash
```

That value is also what a security team needs to whitelist Winnow with an AppLocker or WDAC hash
rule. It changes with every release, so the rule has to be updated each time — that is the
practical cost of shipping unsigned.

#### Whitelisting on machines you manage

For endpoints under your own RMM or GPO, there is a middle path that costs nothing: sign the exe
with a self-signed certificate and push that root into Trusted Publishers on the machines you
manage. Publisher rules then work on those endpoints without buying anything. It does nothing for
machines you do not manage, and deploying a root certificate across a customer estate is a
decision that should be theirs to agree to explicitly, not something to do quietly.

## Updates

A few seconds after launch the app checks GitHub for a newer release and, if there is one, shows a
link in the status bar. It never downloads or installs anything — updating is deliberately your
decision.

The check fails silently. Offline machines, outbound-blocked networks and GitHub rate limits are
all normal in this app's environment and none of them are worth interrupting an investigation. It
uses the system proxy, since a SYSTEM process has no per-user proxy configuration.
