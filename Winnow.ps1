#Requires -Version 5.1
Set-StrictMode -Version Latest

#region 1 - Assemblies, Visual Styles, Host Environment
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

# Bump alongside the version in each release tag (see README > Releasing a new version).
$script:AppVersion        = '2.1.1'
$script:UpdateCheckApiUrl = 'https://api.github.com/repos/theknoxtech/winnow/releases/latest'
$script:LatestReleaseUrl  = $null
$script:currentResults    = $null

# --- Host environment --------------------------------------------------------
# ScreenConnect Backstage runs its processes on a separate desktop object, normally as SYSTEM.
# Detecting that lets the UI route around the things which misbehave there - shell file dialogs,
# launching a browser - rather than failing in ways that are painful to diagnose over a remote
# session. Every part of this is best-effort: if detection fails the app treats itself as an
# ordinary interactive session, which is the safe default because it leaves the elevation
# warnings switched on.
try {
    Add-Type -Namespace Winnow -Name Desktop -ErrorAction Stop -MemberDefinition @'
[DllImport("user32.dll", SetLastError = true)]
public static extern IntPtr GetThreadDesktop(uint dwThreadId);

[DllImport("kernel32.dll")]
public static extern uint GetCurrentThreadId();

[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
public static extern bool GetUserObjectInformation(
    IntPtr hObj, int nIndex, System.Text.StringBuilder pvInfo, uint nLength, out uint lpnLengthNeeded);
'@
} catch {
    # Already defined (the script was run twice in one session), or Add-Type is unavailable.
    # Get-CurrentDesktopName checks for the type before using it either way.
}

function Get-CurrentDesktopName {
    try {
        if (-not ('Winnow.Desktop' -as [type])) { return '' }
        $handle = [Winnow.Desktop]::GetThreadDesktop([Winnow.Desktop]::GetCurrentThreadId())
        if ($handle -eq [IntPtr]::Zero) { return '' }

        # UOI_NAME = 2. Length is in bytes, so 256 chars is 512.
        $buffer = New-Object System.Text.StringBuilder 256
        $needed = 0
        if ([Winnow.Desktop]::GetUserObjectInformation($handle, 2, $buffer, 512, [ref]$needed)) {
            return $buffer.ToString()
        }
    } catch { }
    return ''
}

$script:IsSystemAccount = $false
$script:isAdmin         = $false
$script:UserName        = ''
try {
    $identity               = [Security.Principal.WindowsIdentity]::GetCurrent()
    $script:UserName        = $identity.Name
    $script:IsSystemAccount = $identity.IsSystem
    $script:isAdmin         = $identity.IsSystem -or
        (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)
} catch { }

$script:DesktopName        = Get-CurrentDesktopName
$script:IsAlternateDesktop = [bool]$script:DesktopName -and ($script:DesktopName -ne 'Default')

# Either signal alone is enough. A SYSTEM process has the browser and profile problems whatever
# desktop it sits on; an alternate desktop has the shell-dialog problems whatever account runs it.
$script:IsBackstage = $script:IsSystemAccount -or $script:IsAlternateDesktop

# Fixed, predictable path so a technician can retrieve an export with ScreenConnect file transfer
# without having to be told where to look.
$script:FallbackExportDir = Join-Path ([System.IO.Path]::GetTempPath()) 'Winnow'

function Get-HostDescription {
    $who = if ($script:IsSystemAccount) { 'SYSTEM' }
           elseif ($script:UserName)    { $script:UserName }
           else                         { 'user' }
    if (-not $script:IsSystemAccount -and $script:isAdmin) { $who += ' (elevated)' }

    if ($script:IsAlternateDesktop) {
        $where = if ($script:DesktopName) { "$($script:DesktopName) desktop" } else { 'alternate desktop' }
        return "$who - $where"
    }
    return $who
}
#endregion

#region 2 - Constants and Presets
$script:LogSources = @(
    'Application'
    'System'
    'Security'
    'Setup'
    'Windows PowerShell'
    'Microsoft-Windows-PrintService/Operational'
    'Microsoft-Windows-TerminalServices-RemoteConnectionManager/Operational'
    'Microsoft-Windows-PowerShell/Operational'
    'Microsoft-Windows-Windows Defender/Operational'
    'Directory Service'
    'DFS Replication'
    'DNS Server'
    'Microsoft-Windows-Kernel-PnP/Configuration'
)

$script:LevelMap = [ordered]@{
    'Any'         = $null
    'Critical'    = 1
    'Error'       = 2
    'Warning'     = 3
    'Information' = 4
    'Verbose'     = 5
}

# Presets with optional LogNames2 for multi-log searches
$script:Presets = @(
    [ordered]@{ Group='System Changes'; Label='Software Installs';  LogName='Application'; Id=@(11707,1033,1034);          Description='MsiInstaller product install/remove' }
    [ordered]@{ Group='System Changes'; Label='Service Changes';    LogName='System';      Id=@(7045,7036);                 Description='New service installed or state changed' }
    [ordered]@{ Group='System Changes'; Label='Driver Installs';    LogName='System';      Id=@(7045); MessageFilter='driver'; Description='Kernel/file system driver installed (ID 7045 is shared by every new service; filtered here to entries whose Service Type mentions "driver")' }
    [ordered]@{ Group='System Changes'; Label='Startup/Shutdown';   LogName='System';      Id=@(6005,6006,1074,6008);       Description='Boot, clean shutdown, unexpected shutdown, restart reason' }
    [ordered]@{ Group='Account/Policy'; Label='User Acct Changes';  LogName='Security';    Id=@(4720,4722,4725,4726,4738); Description='Account created, enabled, disabled, deleted, modified' }
    [ordered]@{ Group='Account/Policy'; Label='Policy Changes';     LogName='Security';    Id=@(4719,4739);                 Description='System and domain audit policy changed' }
    [ordered]@{ Group='Account/Policy'; Label='Logon Events';       LogName='Security';    Id=@(4624,4625,4634,4647);       Description='Successful/failed logon and logoff' }
    [ordered]@{ Group='Account/Policy'; Label='Account Lockouts';   LogName='Security';    Id=@(4740);                      Description='User account locked out after failed logon attempts' }
    [ordered]@{ Group='Account/Policy'; Label='Group Membership Chg'; LogName='Security';  Id=@(4728,4729,4732,4733,4756,4757); Description='Members added/removed: global, local, universal security groups' }
    [ordered]@{ Group='Account/Policy'; Label='Kerberos Auth';      LogName='Security';    Id=@(4768,4769,4771,4776);       Description='TGT/service-ticket requests, pre-auth failures, credential validation' }
    [ordered]@{ Group='Account/Policy'; Label='Explicit Credential'; LogName='Security';   Id=@(4648);                      Description='Logon using explicit credentials (RunAs) - possible lateral movement' }
    [ordered]@{ Group='Account/Policy'; Label='Special Privileges'; LogName='Security';    Id=@(4672);                      Description='Admin-equivalent logon - sensitive privileges assigned' }
    [ordered]@{ Group='Account/Policy'; Label='Scheduled Task Chg'; LogName='Security';    Id=@(4698,4699,4700,4701,4702);  Description='Scheduled task created, deleted, enabled, disabled, or updated' }
    [ordered]@{ Group='Account/Policy'; Label='Audit Log Cleared';  LogName='Security';    Id=@(1102);                      Description='Security audit log was cleared - investigate immediately' }
    [ordered]@{ Group='Account/Policy'; Label='PS Script Block Log'; LogName='Microsoft-Windows-PowerShell/Operational'; Id=@(4104); Description='Logged PowerShell script block text (requires Script Block Logging GPO)' }
    [ordered]@{ Group='Account/Policy'; Label='Defender Detections'; LogName='Microsoft-Windows-Windows Defender/Operational'; Id=@(1116,1117); Description='Malware detected / remediation action taken' }
    [ordered]@{ Group='App Health';     Label='App Crashes';        LogName='Application'; Id=@(1000,1002);                 Description='Application Error and Application Hang (WER)' }
    [ordered]@{ Group='App Health';     Label='App Hangs';          LogName='Application'; Id=@(1002,1001);                 Description='Hang detection and Windows Error Reporting follow-up' }
    [ordered]@{ Group='Resources';      Label='Resource/Memory';    LogName='System';      Id=@(2004,2019,2020); LogName2='Application'; Id2=@(1530); Description='Low memory / pool exhaustion / profile warnings' }
    [ordered]@{ Group='Resources';      Label='Disk Errors';        LogName='System';      Id=@(7,11,153); ProviderName=@('disk','Microsoft-Windows-Disk'); Description='Bad block, device I/O error, disk reset - IDs 7/11/153 are also reused by unrelated providers (e.g. Hyper-V networking, Kernel-Boot), so this is scoped to the disk drivers specifically' }
    [ordered]@{ Group='Printing';       Label='Print Jobs';         LogName='Microsoft-Windows-PrintService/Operational'; Id=@(307);         Description='Document printed — job, user, printer, pages' }
    [ordered]@{ Group='Printing';       Label='Print Errors';       LogName='Microsoft-Windows-PrintService/Operational'; Id=@(372,374,375); Description='Spooler errors and failed print jobs' }
    [ordered]@{ Group='Printing';       Label='Spooler Events';     LogName='System';      Id=@(7031,7034); MessageFilter='Spooler'; Description='Print Spooler service crash or restart (IDs 7031/7034 are generic Service Control Manager events shared by every service, filtered here to Spooler by message text)' }
    [ordered]@{ Group='Networking';     Label='Network Changes';    LogName='System';      Id=@(10000,10001,4000,4001);     Description='NIC connect/disconnect (NDIS)' }
    [ordered]@{ Group='Networking';     Label='DHCP Events';        LogName='System';      Id=@(1001,1002,1003);            Description='DHCP lease obtained, renewed, or lost' }
    [ordered]@{ Group='Networking';     Label='DNS Errors';         LogName='System';      Id=@(1014); LogName2='Application'; Id2=@(4015); Description='DNS name resolution failure and DNS server errors' }
    [ordered]@{ Group='Networking';     Label='Firewall Changes';   LogName='Security';    Id=@(4946,4947,4950,2004);       Description='Firewall rule added, modified, or exception changed' }
    [ordered]@{ Group='Networking';     Label='RDP Connections';    LogName='Microsoft-Windows-TerminalServices-RemoteConnectionManager/Operational'; Id=@(261,1149); Description='RDP session auth and successful connections' }
    [ordered]@{ Group='Networking';     Label='VPN / Dial-up';      LogName='Application'; Id=@(20227,20226);               Description='RAS/VPN connection success or failure' }
    [ordered]@{ Group='Active Directory'; Label='AD Replication (All)';  LogName='Directory Service'; Description='All Directory Service log entries - replication/health issues (Domain Controllers only)' }
    [ordered]@{ Group='Active Directory'; Label='DFS Replication (All)'; LogName='DFS Replication';   Description='All DFSR entries - SYSVOL/DFS replication issues (Domain Controllers only)' }
    [ordered]@{ Group='Active Directory'; Label='DNS Server (All)';      LogName='DNS Server';         Description='All DNS Server role entries (Domain Controllers/DNS role only)' }
    [ordered]@{ Group='Hardware';       Label='Hardware Errors (WHEA)'; LogName='System'; Id=@(1); ProviderName=@('Microsoft-Windows-WHEA-Logger','Microsoft-Windows-Kernel-WHEA'); Description='Fatal/corrected hardware errors via WHEA - ID 1 alone is one of the most-reused IDs in the System log, so this is scoped to the WHEA providers specifically' }
    [ordered]@{ Group='Hardware';       Label='Device Install/Removal'; LogName='Microsoft-Windows-Kernel-PnP/Configuration'; Id=@(400,410,420,430); Description='Device driver install/removal lifecycle - tip: use Keyword box to filter by device type (e.g. "USB")' }
    [ordered]@{ Group='Hardware';       Label='Unexpected Shutdown';    LogName='System'; Id=@(41); ProviderName='Microsoft-Windows-Kernel-Power'; Description='Kernel-Power: system rebooted without a clean shutdown - often power/hardware related' }
    [ordered]@{ Group='Hardware';       Label='BSOD / Bugcheck';        LogName='System'; Id=@(1001); ProviderName='Microsoft-Windows-WER-SystemErrorReporting'; Description='Windows Stop Error (blue screen) - bugcheck code and parameters' }
)

$script:SecurityIdentityIds = @(4624,4625,4634,4647,4648,4672,4720,4722,4725,4726,4738,4728,4729,4732,4733,4756,4757,4740,4768,4769,4771,4776)

$script:GroupColors = @{
    'System Changes' = [System.Drawing.Color]::FromArgb(220,235,252)
    'Account/Policy' = [System.Drawing.Color]::FromArgb(255,235,220)
    'App Health'     = [System.Drawing.Color]::FromArgb(255,220,220)
    'Resources'      = [System.Drawing.Color]::FromArgb(255,248,210)
    'Printing'       = [System.Drawing.Color]::FromArgb(230,245,230)
    'Networking'     = [System.Drawing.Color]::FromArgb(235,225,255)
    'Active Directory' = [System.Drawing.Color]::FromArgb(225,225,235)
    'Hardware'       = [System.Drawing.Color]::FromArgb(210,240,240)
}

# Captured before any overrides are applied. Reloading presets.json always starts again from this
# clean set, so repeatedly reloading cannot stack overrides on top of already-overridden presets.
$script:BuiltInPresets = @($script:Presets)
$script:PresetWarning  = ''
#endregion

#region 3 - Helper Functions

# --- Preset overrides --------------------------------------------------------
# Presets live inside the script, and therefore inside the exe, so once compiled they cannot be
# edited in place. An optional presets.json beside the executable is what makes them editable
# without a rebuild: an external file is readable whatever is compiled in, so this works
# identically for Winnow.exe and Winnow.ps1.

function Get-AppDirectory {
    # A ps2exe build is its own process, so the executable's own path is the right answer. Run as
    # a .ps1 the process is powershell.exe, and $PSScriptRoot is what we want instead.
    try {
        $procPath = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
        $leaf     = [System.IO.Path]::GetFileNameWithoutExtension($procPath)
        if ($leaf -notmatch '^(powershell|pwsh|powershell_ise)$') {
            return [System.IO.Path]::GetDirectoryName($procPath)
        }
    } catch { }

    if ($PSScriptRoot) { return $PSScriptRoot }
    return (Get-Location).Path
}

$script:PresetFilePath = Join-Path (Get-AppDirectory) 'presets.json'

# Maps the lower-cased JSON field names to the PascalCase keys the preset hashtables use.
$script:PresetFieldMap = @{
    group        = 'Group'
    label        = 'Label'
    description  = 'Description'
    logname      = 'LogName'
    id           = 'Id'
    logname2     = 'LogName2'
    id2          = 'Id2'
    providername = 'ProviderName'
    messagefilter = 'MessageFilter'
}

function Get-JsonFieldTable {
    # JSON field names are case-sensitive to look up on a PSCustomObject, which would make
    # "logName" and "logname" behave differently in a hand-edited file. Flattening to a
    # lower-cased hashtable first means the user's capitalisation does not matter.
    param([Parameter(Mandatory)] $Object)

    $table = @{}
    foreach ($prop in $Object.PSObject.Properties) {
        $table[$prop.Name.ToLowerInvariant()] = $prop.Value
    }
    return $table
}

function Read-PresetOverrideEntry {
    # Returns the entries from presets.json, or $null with $script:PresetWarning set. A typo in
    # that file should cost the user their overrides, not the whole application.
    if (-not (Test-Path $script:PresetFilePath)) { return $null }

    try {
        $raw = Get-Content $script:PresetFilePath -Raw -ErrorAction Stop
        if (-not $raw -or -not $raw.Trim()) { return $null }
        $doc = $raw | ConvertFrom-Json -ErrorAction Stop
    } catch {
        $reason = ($_.Exception.Message -split "`n")[0]
        $script:PresetWarning = "presets.json could not be read ($reason) - using built-in presets."
        return $null
    }

    $fields = Get-JsonFieldTable -Object $doc
    if (-not $fields.ContainsKey('presets')) {
        $script:PresetWarning = 'presets.json has no "presets" array - using built-in presets.'
        return $null
    }
    return @($fields['presets'])
}

function ConvertTo-Preset {
    # Builds one preset from an override entry. Starting from $Existing is what lets an entry
    # name only the fields it changes, so adding an Event ID does not mean restating the preset.
    param(
        [hashtable] $Fields,
        $Existing
    )

    $preset = [ordered]@{}
    if ($Existing) {
        foreach ($key in $Existing.Keys) { $preset[$key] = $Existing[$key] }
    }

    foreach ($jsonName in $Fields.Keys) {
        if (-not $script:PresetFieldMap.ContainsKey($jsonName)) { continue }
        $key   = $script:PresetFieldMap[$jsonName]
        $value = $Fields[$jsonName]

        # JSON numbers arrive as Int64, and the event log filter hashtable wants Int32. The null
        # filtering is not defensive padding: a cleared list is written as JSON null, and a
        # hand-edited file can hold [null] or a stray empty string. Without it, null would cast
        # to a real Event ID of 0 and quietly return nothing for the rest of that preset's life.
        if ($key -eq 'Id' -or $key -eq 'Id2') {
            $value = @($value | Where-Object { $null -ne $_ -and "$_".Trim() } | ForEach-Object { [int]$_ })
        } elseif ($key -eq 'ProviderName') {
            $value = @($value | Where-Object { $null -ne $_ -and "$_".Trim() })
        }

        $preset[$key] = $value
    }

    if (-not $preset.Contains('Group') -or -not $preset['Group']) { $preset['Group'] = 'Custom' }
    if (-not $preset.Contains('Description'))                     { $preset['Description'] = '' }
    return $preset
}

function Merge-PresetEntry {
    # Applies one presets.json entry to the working list, in place. A label matching an existing
    # preset changes it, an unrecognised label adds one, and "disabled": true removes one.
    param(
        [Parameter(Mandatory)] [System.Collections.Generic.List[object]] $Presets,
        [Parameter(Mandatory)] [hashtable] $Fields
    )

    $label = $Fields['label']
    if (-not $label) { return }

    $index = -1
    for ($i = 0; $i -lt $Presets.Count; $i++) {
        if ($Presets[$i]['Label'] -eq $label) { $index = $i; break }
    }

    if ($Fields.ContainsKey('disabled') -and $Fields['disabled']) {
        if ($index -ge 0) { $Presets.RemoveAt($index) }
        return
    }

    $existing = if ($index -ge 0) { $Presets[$index] } else { $null }
    $preset   = ConvertTo-Preset -Fields $Fields -Existing $existing

    if (-not $preset.Contains('LogName') -or -not $preset['LogName']) {
        $script:PresetWarning = "Preset '$label' in presets.json has no logName - skipped."
        return
    }

    if ($index -ge 0) { $Presets[$index] = $preset } else { $null = $Presets.Add($preset) }
}

function Import-PresetOverrides {
    # Merges presets.json over the built-in set. Always restarts from the built-ins so that
    # reloading repeatedly cannot stack overrides on top of already-overridden presets.
    $script:PresetWarning = ''
    $script:Presets       = @($script:BuiltInPresets)

    $entries = Read-PresetOverrideEntry
    if (-not $entries) { return }

    $merged = [System.Collections.Generic.List[object]]::new()
    foreach ($preset in $script:Presets) { $null = $merged.Add($preset) }

    foreach ($entry in $entries) {
        if ($null -eq $entry) { continue }
        Merge-PresetEntry -Presets $merged -Fields (Get-JsonFieldTable -Object $entry)
    }

    $script:Presets = @($merged)
}

function New-PresetTemplateFile {
    # The examples sit outside the "presets" array on purpose, so creating this file changes
    # nothing until someone deliberately moves one in. JSON has no comments, hence the _ keys.
    $template = @'
{
  "_comment": "Overrides for Winnow's built-in presets. Match a built-in by \"label\" to change it, listing only the fields you want to change. An unrecognised \"label\" adds a new preset. \"disabled\": true hides one. Nothing under _examples takes effect - move an entry into \"presets\" to use it.",

  "_examples": [
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
  ],

  "presets": []
}
'@
    Set-Content -Path $script:PresetFilePath -Value $template -Encoding UTF8
}

# --- Writing overrides back out ----------------------------------------------
# The inverse of the merge above. The editor hands back the whole preset set; what gets written
# is only what differs from the built-ins, so presets.json stays small, diffs cleanly, and a
# preset the user never touched keeps tracking future Winnow releases instead of being pinned
# to whatever the built-in happened to be on the day they opened the editor.

# Field write order. Fixed so a saved file has a stable shape rather than reordering itself on
# every save, which would make every diff look like a rewrite.
$script:PresetJsonOrder = @(
    'group', 'label', 'logname', 'id', 'providername',
    'messagefilter', 'logname2', 'id2', 'description'
)

function Get-BuiltInPreset {
    param([string] $Label)
    foreach ($preset in $script:BuiltInPresets) {
        if ($preset['Label'] -eq $Label) { return $preset }
    }
    return $null
}

function Copy-Preset {
    # Presets are copied into the editor so that cancelling really does discard everything. A
    # shallow copy is not enough: the Id and ProviderName arrays would still be shared with the
    # live set, and editing one would change the other.
    param($Preset)

    $copy = [ordered]@{}
    foreach ($key in $Preset.Keys) {
        $value = $Preset[$key]
        # Assigned from an if statement, not by one. A block that emits a one-element array has
        # it unrolled to a bare scalar on the way out, which would turn a single-Event-ID preset
        # into one whose Id is an int, and an empty list into $null. A direct assignment does not.
        if ($value -is [System.Array]) {
            $copy[$key] = @($value)
        } else {
            $copy[$key] = $value
        }
    }
    return $copy
}

function Test-PresetValueEmpty {
    param($Value)
    if ($null -eq $Value)            { return $true }
    if ($Value -is [string])         { return [string]::IsNullOrWhiteSpace($Value) }
    if ($Value -is [System.Array])   { return $Value.Count -eq 0 }
    return $false
}

function Test-PresetValueEqual {
    # ProviderName is a bare string in some built-ins and an array in others, and JSON always
    # brings it back as an array - so both sides are normalised to a list before comparing,
    # otherwise an untouched preset would look modified purely because of its shape.
    param($Left, $Right)

    if (Test-PresetValueEmpty $Left) { return (Test-PresetValueEmpty $Right) }
    if (Test-PresetValueEmpty $Right) { return $false }

    if ($Left -is [System.Array] -or $Right -is [System.Array]) {
        $leftItems  = @($Left)
        $rightItems = @($Right)
        if ($leftItems.Count -ne $rightItems.Count) { return $false }
        for ($i = 0; $i -lt $leftItems.Count; $i++) {
            if ("$($leftItems[$i])" -ne "$($rightItems[$i])") { return $false }
        }
        return $true
    }
    return ("$Left" -eq "$Right")
}

function ConvertTo-PresetOverrideEntry {
    # The smallest entry that reproduces $Preset from $BuiltIn, or $null when nothing differs.
    # A $null $BuiltIn means a custom preset, which has nothing to diff against and is written
    # out in full.
    param($Preset, $BuiltIn)

    $entry   = [ordered]@{}
    $changed = $false

    foreach ($jsonName in $script:PresetJsonOrder) {
        if ($jsonName -eq 'label') { $entry['label'] = $Preset['Label']; continue }

        $key     = $script:PresetFieldMap[$jsonName]
        $current = if ($Preset.Contains($key)) { $Preset[$key] } else { $null }

        if ($null -eq $BuiltIn) {
            if (Test-PresetValueEmpty $current) { continue }
            $entry[$jsonName] = $current
            $changed = $true
            continue
        }

        $original = if ($BuiltIn.Contains($key)) { $BuiltIn[$key] } else { $null }
        if (Test-PresetValueEqual $current $original) { continue }

        # An emptied field is written as an empty value rather than omitted: omitting it would
        # mean "unchanged", and the built-in's value would come straight back on next launch.
        if (-not (Test-PresetValueEmpty $current)) {
            $entry[$jsonName] = $current
        } elseif ($key -eq 'Id' -or $key -eq 'Id2' -or $key -eq 'ProviderName') {
            $entry[$jsonName] = @()
        } else {
            $entry[$jsonName] = ''
        }
        $changed = $true
    }

    if (-not $changed) { return $null }
    return $entry
}

function Export-PresetOverrides {
    # Round-trips with Import-PresetOverrides by construction: whatever this omits is, by
    # definition, identical to the built-in that Import starts from.
    param([Parameter(Mandatory)] $Presets)

    $entries = [System.Collections.Generic.List[object]]::new()
    foreach ($preset in $Presets) {
        $entry = ConvertTo-PresetOverrideEntry -Preset $preset -BuiltIn (Get-BuiltInPreset $preset['Label'])
        if ($entry) { $null = $entries.Add($entry) }
    }

    # A built-in missing from the set was hidden, and that has to be recorded explicitly -
    # simply leaving it out is how the file says "unchanged", which would bring it back.
    $keptLabels = @($Presets | ForEach-Object { $_['Label'] })
    foreach ($builtIn in $script:BuiltInPresets) {
        if ($keptLabels -notcontains $builtIn['Label']) {
            $null = $entries.Add([ordered]@{ label = $builtIn['Label']; disabled = $true })
        }
    }

    $document = [ordered]@{
        '_comment' = 'Written by the Winnow preset editor. Only differences from the built-in presets are stored, so a preset that is absent here simply uses its built-in definition. Hand-editing works fine, but saving from the editor rewrites this file: your changes survive, your formatting and any comments do not.'
        'presets'  = @($entries)
    }

    $json = $document | ConvertTo-Json -Depth 6
    Set-Content -Path $script:PresetFilePath -Value $json -Encoding UTF8
    return $entries.Count
}

# --- Editor field parsing ----------------------------------------------------
# The editor exposes lists as plain comma-separated text, which is far quicker to edit than a
# grid. These turn that text back into the typed values the query builder expects, and throw
# rather than silently discarding a value the user meant to keep.

function Read-IdList {
    param([string] $Text, [string] $FieldName = 'Event IDs')

    $ids = [System.Collections.Generic.List[int]]::new()
    foreach ($token in ($Text -split '[,;\s]+' | Where-Object { $_ })) {
        $parsed = 0
        if (-not [int]::TryParse($token, [ref] $parsed)) {
            throw "$FieldName contains '$token', which is not a number."
        }
        $null = $ids.Add($parsed)
    }
    return ,@($ids)
}

function Read-NameList {
    param([string] $Text)
    return ,@($Text -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

function Format-PresetList {
    param($Value)
    if (Test-PresetValueEmpty $Value) { return '' }
    return (@($Value) -join ', ')
}

# --- Editor rows -------------------------------------------------------------
# One row per preset as the editor sees it. Kept here rather than with the dialog because this
# is data, not UI: a row is a copy of a preset plus whether it is currently switched off.

$script:EditorRows = $null

function New-EditorRow {
    param($Preset, [bool] $Hidden)
    return [pscustomobject]@{ Preset = (Copy-Preset $Preset); Hidden = $Hidden }
}

function New-EditorRowList {
    # The live set plus the built-ins currently hidden by an override. Without the second half
    # there would be no way back: hiding a preset would remove the only place to un-hide it.
    $rows   = [System.Collections.Generic.List[object]]::new()
    $labels = @($script:Presets | ForEach-Object { $_['Label'] })

    foreach ($preset in $script:Presets)        { $null = $rows.Add((New-EditorRow $preset $false)) }
    foreach ($builtIn in $script:BuiltInPresets) {
        if ($labels -notcontains $builtIn['Label']) { $null = $rows.Add((New-EditorRow $builtIn $true)) }
    }
    # Returned with a leading comma so the list survives as a list. Without it the pipeline
    # enumerates it into a fixed-size array, and New, Clone and Delete cannot add or remove.
    return ,$rows
}

function Get-EditorRowStatus {
    param($Row)
    if ($Row.Hidden) { return 'Hidden' }

    $builtIn = Get-BuiltInPreset $Row.Preset['Label']
    if (-not $builtIn) { return 'Custom' }
    if (ConvertTo-PresetOverrideEntry -Preset $Row.Preset -BuiltIn $builtIn) { return 'Modified' }
    return 'Built-in'
}

function New-UniqueEditorLabel {
    # Rows are passed in rather than read from script state, so this stays a pure function of
    # its inputs and can be tested without standing up the dialog.
    param([Parameter(Mandatory)] [string] $Base, $Rows)
    $labels = @($Rows | ForEach-Object { $_.Preset['Label'] })
    if ($labels -notcontains $Base) { return $Base }
    for ($n = 2; $n -lt 500; $n++) {
        if ($labels -notcontains "$Base $n") { return "$Base $n" }
    }
    return "$Base $([guid]::NewGuid().ToString('N').Substring(0,4))"
}

# Applied at startup; the Presets... button re-runs it after an edit.
Import-PresetOverrides

function Test-ForUpdate {
    # Never let this interrupt the user - offline machines, outbound-blocked networks, and
    # GitHub rate limits are all normal here, so any failure is silently ignored.
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

        $params = @{
            Uri         = $script:UpdateCheckApiUrl
            TimeoutSec  = 4
            Headers     = @{ 'User-Agent' = 'Winnow' }
            ErrorAction = 'Stop'
        }

        # Running as SYSTEM there is no per-user proxy configuration, so on a proxied network the
        # check would otherwise silently never fire.
        try {
            $proxyUri = [System.Net.WebRequest]::GetSystemWebProxy().GetProxy($script:UpdateCheckApiUrl)
            if ($proxyUri -and $proxyUri.AbsoluteUri -ne $script:UpdateCheckApiUrl) {
                $params['Proxy']                     = $proxyUri
                $params['ProxyUseDefaultCredentials'] = $true
            }
        } catch {
            # No resolvable proxy configuration; a direct connection is fine.
        }

        $release = Invoke-RestMethod @params

        $latestVersion = [version]($release.tag_name -replace '^v', '')
        $currentVersion = [version]$script:AppVersion
        if ($latestVersion -gt $currentVersion) {
            $script:LatestReleaseUrl = $release.html_url
            return $release.tag_name
        }
    } catch { }
    return $null
}

function Get-EventsForPreset {
    param($Preset, $MaxEvents)
    $results = [System.Collections.Generic.List[object]]::new()

    $fh = @{ LogName = $Preset.LogName }
    if ($Preset.Contains('Id') -and $Preset.Id -and $Preset.Id.Count -gt 0) {
        $fh['Id'] = [int[]]$Preset.Id
    }
    if ($Preset.Contains('ProviderName') -and $Preset.ProviderName) {
        $fh['ProviderName'] = [string[]]$Preset.ProviderName
    }
    try {
        $ev = Get-WinEvent -FilterHashtable $fh -MaxEvents $MaxEvents -ErrorAction Stop
        if ($ev) { $results.AddRange([object[]]$ev) }
    } catch [System.UnauthorizedAccessException] {
        throw
    } catch {
        # Log may not exist on this machine (e.g. PrintService not enabled, or a DC-only log
        # like Directory Service). Get-WinEvent throws EventLogNotFoundException for a missing
        # Event ID within a valid log, but a plain System.Exception for a log name that doesn't
        # exist at all - treat any non-permission failure here as "nothing to show".
    }

    if ($Preset.Contains('LogName2')) {
        $fh2 = @{ LogName = $Preset.LogName2; Id = [int[]]$Preset.Id2 }
        try {
            $ev2 = Get-WinEvent -FilterHashtable $fh2 -MaxEvents $MaxEvents -ErrorAction Stop
            if ($ev2) { $results.AddRange([object[]]$ev2) }
        } catch [System.UnauthorizedAccessException] {
            throw
        } catch { }
    }

    if ($Preset.Contains('MessageFilter') -and $Preset.MessageFilter) {
        # Some Event IDs (e.g. Service Control Manager's generic "service crashed" IDs) are
        # shared across every service on the box, with no distinguishing ProviderName - only
        # the message text says which service. Filter those down to the one this preset means.
        $mf = $Preset.MessageFilter
        $results = [System.Collections.Generic.List[object]]@($results | Where-Object { $_.Message -like "*$mf*" })
    }

    return $results | Sort-Object TimeCreated -Descending
}

function Build-DataTable {
    param([object[]]$Events, [string]$Keyword)
    $dt = New-Object System.Data.DataTable
    $null = $dt.Columns.Add('TimeCreated',      [datetime])
    $null = $dt.Columns.Add('LevelDisplayName', [string])
    $null = $dt.Columns.Add('ProviderName',     [string])
    $null = $dt.Columns.Add('Id',               [int])
    $null = $dt.Columns.Add('Message',          [string])
    $null = $dt.Columns.Add('FullMessage',      [string])

    foreach ($ev in $Events) {
        if ($Keyword -and $ev.Message -notlike "*$Keyword*") { continue }
        $msg = if ($ev.Message) { $ev.Message } else { '' }
        $row = $dt.NewRow()
        $row['TimeCreated']      = $ev.TimeCreated
        $row['LevelDisplayName'] = $ev.LevelDisplayName
        $row['ProviderName']     = $ev.ProviderName
        $row['Id']               = $ev.Id
        $row['Message']          = if ($msg.Length -gt 200) { $msg.Substring(0,200) + '...' } else { $msg }
        $row['FullMessage']      = $msg
        $null = $dt.Rows.Add($row)
    }
    # DataTable implements IListSource, which PowerShell unrolls when returned bare -
    # the unary comma forces it back into a single scalar output.
    return ,$dt
}

function Get-EventsForApp {
    param([string]$AppName, [int]$MaxEvents)
    $results = [System.Collections.Generic.List[object]]::new()
    $seen    = [System.Collections.Generic.HashSet[string]]::new()

    try { $providers = Get-WinEvent -ListProvider "*$AppName*" -ErrorAction SilentlyContinue } catch { $providers = @() }

    $logToProviders = @{}
    foreach ($p in $providers) {
        try {
            foreach ($link in $p.LogLinks) {
                $ln = $link.LogName
                if (-not $logToProviders.ContainsKey($ln)) { $logToProviders[$ln] = [System.Collections.Generic.List[string]]::new() }
                $logToProviders[$ln].Add($p.Name)
            }
        } catch { continue }
    }

    foreach ($logName in $logToProviders.Keys) {
        $fh = @{ LogName = $logName; ProviderName = [string[]]($logToProviders[$logName] | Select-Object -Unique) }
        try {
            foreach ($item in (Get-WinEvent -FilterHashtable $fh -MaxEvents $MaxEvents -ErrorAction Stop)) {
                if ($seen.Add("$($item.LogName)|$($item.RecordId)")) { $results.Add($item) }
            }
        } catch [System.Diagnostics.Eventing.Reader.EventLogNotFoundException] {
        } catch { }
    }

    try {
        $fh2 = @{ LogName = 'Application'; Id = [int[]]@(1000,1001,1002) }
        foreach ($item in (Get-WinEvent -FilterHashtable $fh2 -MaxEvents $MaxEvents -ErrorAction Stop)) {
            if ($item.Message -notlike "*$AppName*") { continue }
            if ($seen.Add("$($item.LogName)|$($item.RecordId)")) { $results.Add($item) }
        }
    } catch [System.Diagnostics.Eventing.Reader.EventLogNotFoundException] { }

    return $results | Sort-Object TimeCreated -Descending | Select-Object -First $MaxEvents
}

function Get-SecurityEventsByIdentity {
    param([string]$UserName, [string]$HostName, [string]$IPAddress, [int]$MaxEvents)
    $fh = @{ LogName = 'Security'; Id = [int[]]$script:SecurityIdentityIds }
    $events = Get-WinEvent -FilterHashtable $fh -MaxEvents $MaxEvents -ErrorAction Stop
    $events | Where-Object {
        ($UserName  -eq '' -or $_.Message -like "*$UserName*")  -and
        ($HostName  -eq '' -or $_.Message -like "*$HostName*")  -and
        ($IPAddress -eq '' -or $_.Message -like "*$IPAddress*")
    } | Sort-Object TimeCreated -Descending
}

function Set-ClipboardTextSafe {
    param([string]$Text)
    try {
        [System.Windows.Forms.Clipboard]::SetText($Text)
        return $true
    } catch {
        # The clipboard is owned per desktop and can be locked by another process; on an alternate
        # desktop it may not be usable at all. Callers always show the value on screen as well, so
        # failing quietly here still leaves the user something to work with.
        return $false
    }
}

function Get-ExportPath {
    # Resolves where a CSV export should be written, or $null if the user cancelled.
    #
    # On a Backstage desktop the shell save dialog is skipped outright: common file dialogs depend
    # on the shell, which is not reliably available on an alternate desktop running as SYSTEM, so
    # it can fail or hang rather than appear. A fixed path is also more useful there, because the
    # file gets retrieved over ScreenConnect file transfer regardless.
    param([Parameter(Mandatory)] [string] $FileName)

    if (-not $script:IsBackstage) {
        try {
            $dialog          = New-Object System.Windows.Forms.SaveFileDialog
            $dialog.Filter   = 'CSV Files (*.csv)|*.csv|All Files (*.*)|*.*'
            $dialog.FileName = $FileName
            if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { return $null }
            return $dialog.FileName
        } catch {
            # Dialog unavailable after all - fall through to the fixed path rather than give up.
        }
    }

    if (-not (Test-Path $script:FallbackExportDir)) {
        $null = New-Item -ItemType Directory -Path $script:FallbackExportDir -Force
    }
    return (Join-Path $script:FallbackExportDir $FileName)
}

function Invoke-Export {
    param([object[]] $Results)

    $fileName = "Winnow_$(Get-Date -Format 'yyyyMMdd_HHmmss').csv"
    $path     = Get-ExportPath -FileName $fileName
    if (-not $path) { return }

    $usedFallback = $path.StartsWith($script:FallbackExportDir, [StringComparison]::OrdinalIgnoreCase)

    try {
        $Results | Select-Object TimeCreated, LevelDisplayName, ProviderName, Id, Message |
            Export-Csv -Path $path -NoTypeInformation -Encoding UTF8

        $message = "Exported $($Results.Count) record(s) to:`n$path"
        if ($usedFallback) {
            if (Set-ClipboardTextSafe $path) {
                $message += "`n`nThe path has been copied to the clipboard."
            }
            $message += "`n`nRetrieve the file with ScreenConnect file transfer."
        }

        [System.Windows.Forms.MessageBox]::Show(
            $message, 'Export Complete',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Information)
    } catch {
        [System.Windows.Forms.MessageBox]::Show(
            "Export failed:`n$($_.Exception.Message)", 'Export Failed',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error)
    }
}
#endregion

#region 4 - Event Query Execution
# NOTE: an earlier version of this used System.ComponentModel.BackgroundWorker to run
# queries off the UI thread. PowerShell scriptblocks cannot execute as .NET delegates on a
# thread with no Runspace attached (BackgroundWorker.DoWork raises on a raw ThreadPool
# thread), so that always failed with "There is no Runspace available to run scripts in
# this thread" the instant it ran - the setup can't happen inside the handler itself,
# since the failure occurs before the first statement of the scriptblock executes.
# Queries run synchronously instead; Application.DoEvents() keeps the UI painting.
function New-QueryArgument {
    # Every caller builds the same shape. The script runs under Set-StrictMode -Version Latest,
    # where reading a key a hashtable does not have is a terminating error - so a caller that
    # omitted the keys it had no use for made Invoke-EventQuery throw the moment it tested one.
    param(
        $Preset,
        $FilterHash,
        [string] $AppName,
        $SecurityIdentity,
        [int]    $MaxEvents,
        [string] $Keyword
    )
    return @{
        Preset           = $Preset
        FilterHash       = $FilterHash
        AppName          = $AppName
        SecurityIdentity = $SecurityIdentity
        MaxEvents        = $MaxEvents
        Keyword          = $Keyword
    }
}

function Invoke-EventQuery {
    # Reads its argument by index, not by property. Index access on a missing key yields $null
    # under StrictMode; property access throws. New-QueryArgument should make that impossible
    # anyway, but this is the place the failure actually surfaced, so it is guarded here too.
    param($Argument)
    $kw = $Argument['Keyword']
    try {
        if ($Argument['Preset']) {
            $events = Get-EventsForPreset -Preset $Argument['Preset'] -MaxEvents $Argument['MaxEvents']
            return @{ Events = $events; Keyword = $kw }
        } elseif ($Argument['AppName']) {
            $events = Get-EventsForApp -AppName $Argument['AppName'] -MaxEvents $Argument['MaxEvents']
            return @{ Events = $events; Keyword = $kw }
        } elseif ($Argument['SecurityIdentity']) {
            $si     = $Argument['SecurityIdentity']
            $events = Get-SecurityEventsByIdentity -UserName $si['UserName'] -HostName $si['HostName'] -IPAddress $si['IPAddress'] -MaxEvents $Argument['MaxEvents']
            return @{ Events = $events; Keyword = $kw }
        } else {
            $events = Get-WinEvent -FilterHashtable $Argument['FilterHash'] -MaxEvents $Argument['MaxEvents'] -ErrorAction Stop
            return @{ Events = $events; Keyword = $kw }
        }
    } catch [System.Diagnostics.Eventing.Reader.EventLogNotFoundException] {
        return @{ Error = "Log not found: $($_.Exception.Message)`n`nCheck that the log name is correct." }
    } catch [System.UnauthorizedAccessException] {
        return @{ Error = "Access denied.`n`nThe Security log requires Administrator privileges.`nRight-click the script and choose 'Run as Administrator'." }
    } catch {
        if ($_.Exception.Message -like '*No events*' -or $_.Exception.HResult -eq -2147024816) {
            return @{ Events = @(); Keyword = $kw }
        } else {
            return @{ Error = $_.Exception.Message }
        }
    }
}

# Show-SearchResults and Invoke-QuerySync are wired after UI is built (reference UI controls)
#endregion

#region 5 - UI Construction

# --- Main Form ---
# Sized against the desktop it actually lands on, rather than a fixed 1150x780 with a 900x600
# minimum. A ScreenConnect Backstage desktop is commonly 1024x768 and can be smaller; the old
# fixed size opened with the status bar and part of the detail pane off-screen, and the 900x600
# minimum then prevented shrinking it back into view.
$workArea = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$minW     = 720
$minH     = 480
$fitW     = [Math]::Max($minW, $workArea.Width  - 40)
$fitH     = [Math]::Max($minH, $workArea.Height - 40)

# Preset strip height, capped so that 36 wrapped buttons cannot dictate the window's minimum
# height - the specific thing that made this unusable on a small desktop. It scrolls past that.
$presetStripHeight = if ($workArea.Height -lt 800) { 84 } else { 112 }

$mainForm                  = New-Object System.Windows.Forms.Form
$mainForm.Text             = 'Winnow'
$mainForm.MinimumSize      = New-Object System.Drawing.Size($minW, $minH)
$mainForm.Size             = New-Object System.Drawing.Size([int][Math]::Min(1150, $fitW), [int][Math]::Min(780, $fitH))
$mainForm.StartPosition    = 'CenterScreen'
$mainForm.Font             = New-Object System.Drawing.Font('Segoe UI', 9)

# --- Root layout: 4 rows (filter, presets, splitter, status) ---
$rootTable                      = New-Object System.Windows.Forms.TableLayoutPanel
$rootTable.Dock                 = 'Fill'
$rootTable.RowCount             = 4
$rootTable.ColumnCount          = 1
$null = $rootTable.RowStyles.Add((New-Object System.Windows.Forms.RowStyle([System.Windows.Forms.SizeType]::AutoSize)))
# Presets row is a fixed height rather than AutoSize, so the strip scrolls instead of growing
# without limit and pushing the results grid off a short desktop.
$null = $rootTable.RowStyles.Add((New-Object System.Windows.Forms.RowStyle([System.Windows.Forms.SizeType]::Absolute, $presetStripHeight)))
$null = $rootTable.RowStyles.Add((New-Object System.Windows.Forms.RowStyle([System.Windows.Forms.SizeType]::Percent, 100)))
$null = $rootTable.RowStyles.Add((New-Object System.Windows.Forms.RowStyle([System.Windows.Forms.SizeType]::AutoSize)))
$mainForm.Controls.Add($rootTable)

# --- Filter Panel ---
$pnlFilter              = New-Object System.Windows.Forms.Panel
$pnlFilter.Dock         = 'Fill'
$pnlFilter.AutoSize     = $true
$pnlFilter.AutoSizeMode = 'GrowAndShrink'
$pnlFilter.Padding      = New-Object System.Windows.Forms.Padding(6, 6, 6, 4)
$pnlFilter.BackColor    = [System.Drawing.Color]::FromArgb(245,245,248)

$filterRow1             = New-Object System.Windows.Forms.FlowLayoutPanel
$filterRow1.Dock        = 'Top'
$filterRow1.AutoSize    = $true
$filterRow1.WrapContents= $false
$filterRow1.Padding     = New-Object System.Windows.Forms.Padding(0,0,0,4)

$filterRow2             = New-Object System.Windows.Forms.FlowLayoutPanel
$filterRow2.Dock        = 'Top'
$filterRow2.AutoSize    = $true
$filterRow2.WrapContents= $false

$filterRow3             = New-Object System.Windows.Forms.FlowLayoutPanel
$filterRow3.Dock        = 'Top'
$filterRow3.AutoSize    = $true
$filterRow3.WrapContents= $false
$filterRow3.Padding     = New-Object System.Windows.Forms.Padding(0,4,0,0)

$filterRow4             = New-Object System.Windows.Forms.FlowLayoutPanel
$filterRow4.Dock        = 'Top'
$filterRow4.AutoSize    = $true
$filterRow4.WrapContents= $false
$filterRow4.Padding     = New-Object System.Windows.Forms.Padding(0,4,0,0)

function New-Label($text) {
    $l = New-Object System.Windows.Forms.Label
    $l.Text      = $text
    $l.AutoSize  = $true
    $l.Anchor    = 'Left'
    $l.Margin    = New-Object System.Windows.Forms.Padding(0,6,2,0)
    return $l
}

# Row 1: Log, Level, Event ID, Max Events
$cboLogSource               = New-Object System.Windows.Forms.ComboBox
$cboLogSource.DropDownStyle = 'DropDown'
$cboLogSource.Width         = 200
$cboLogSource.Margin        = New-Object System.Windows.Forms.Padding(0,2,10,0)
foreach ($src in $script:LogSources) { $null = $cboLogSource.Items.Add($src) }
$cboLogSource.SelectedIndex = 0

$cboLevel               = New-Object System.Windows.Forms.ComboBox
$cboLevel.DropDownStyle = 'DropDownList'
$cboLevel.Width         = 110
$cboLevel.Margin        = New-Object System.Windows.Forms.Padding(0,2,10,0)
foreach ($key in $script:LevelMap.Keys) { $null = $cboLevel.Items.Add($key) }
$cboLevel.SelectedIndex = 0

$txtEventId         = New-Object System.Windows.Forms.TextBox
$txtEventId.Width   = 130
$txtEventId.Margin  = New-Object System.Windows.Forms.Padding(0,2,10,0)

$nudMaxEvents           = New-Object System.Windows.Forms.NumericUpDown
$nudMaxEvents.Minimum   = 100
$nudMaxEvents.Maximum   = 50000
$nudMaxEvents.Value     = 1000
$nudMaxEvents.Increment = 500
$nudMaxEvents.Width     = 80
$nudMaxEvents.Margin    = New-Object System.Windows.Forms.Padding(0,2,0,0)

foreach ($pair in @(
    @((New-Label 'Log:'),       $cboLogSource)
    @((New-Label 'Level:'),     $cboLevel)
    @((New-Label 'Event ID:'),  $txtEventId)
    @((New-Label 'Max Events:'),$nudMaxEvents)
)) { foreach ($ctrl in $pair) { $filterRow1.Controls.Add($ctrl) } }

# Row 2: Keyword, From, To, Buttons
$txtKeyword         = New-Object System.Windows.Forms.TextBox
$txtKeyword.Width   = 260
$txtKeyword.Margin  = New-Object System.Windows.Forms.Padding(0,2,10,0)

$dtpFrom              = New-Object System.Windows.Forms.DateTimePicker
$dtpFrom.Format       = 'Custom'
$dtpFrom.CustomFormat = 'yyyy-MM-dd HH:mm'
$dtpFrom.ShowCheckBox = $true
$dtpFrom.Checked      = $false
$dtpFrom.Width        = 155
$dtpFrom.Margin       = New-Object System.Windows.Forms.Padding(0,2,10,0)
$dtpFrom.Value        = (Get-Date).AddDays(-7)

$dtpTo              = New-Object System.Windows.Forms.DateTimePicker
$dtpTo.Format       = 'Custom'
$dtpTo.CustomFormat = 'yyyy-MM-dd HH:mm'
$dtpTo.ShowCheckBox = $true
$dtpTo.Checked      = $false
$dtpTo.Width        = 155
$dtpTo.Margin       = New-Object System.Windows.Forms.Padding(0,2,10,0)
$dtpTo.Value        = Get-Date

$btnSearch              = New-Object System.Windows.Forms.Button
$btnSearch.Text         = 'Search'
$btnSearch.Width        = 80
$btnSearch.Height       = 26
$btnSearch.BackColor    = [System.Drawing.Color]::DodgerBlue
$btnSearch.ForeColor    = [System.Drawing.Color]::White
$btnSearch.FlatStyle    = 'Flat'
$btnSearch.Margin       = New-Object System.Windows.Forms.Padding(0,2,6,0)

$btnClear           = New-Object System.Windows.Forms.Button
$btnClear.Text      = 'Clear'
$btnClear.Width     = 70
$btnClear.Height    = 26
$btnClear.Margin    = New-Object System.Windows.Forms.Padding(0,2,6,0)

$btnExport          = New-Object System.Windows.Forms.Button
$btnExport.Text     = 'Export CSV'
$btnExport.Width    = 90
$btnExport.Height   = 26
$btnExport.Enabled  = $false
$btnExport.Margin   = New-Object System.Windows.Forms.Padding(0,2,6,0)

$btnPresets         = New-Object System.Windows.Forms.Button
$btnPresets.Text    = 'Presets...'
$btnPresets.Width   = 80
$btnPresets.Height  = 26
$btnPresets.Margin  = New-Object System.Windows.Forms.Padding(0,2,0,0)

# Descriptions are long enough to be worth a tooltip rather than crowding the button face.
$toolTip             = New-Object System.Windows.Forms.ToolTip
$toolTip.AutoPopDelay = 20000
$toolTip.InitialDelay = 400
$toolTip.ReshowDelay  = 200
$toolTip.SetToolTip($btnPresets, "Add, change, hide or test presets")

foreach ($pair in @(
    @((New-Label 'Keyword:'), $txtKeyword)
    @((New-Label 'From:'),    $dtpFrom)
    @((New-Label 'To:'),      $dtpTo)
    @($btnSearch)
    @($btnClear)
    @($btnExport)
    @($btnPresets)
)) { foreach ($ctrl in $pair) { $filterRow2.Controls.Add($ctrl) } }

# Row 3: Application search
$txtAppName         = New-Object System.Windows.Forms.TextBox
$txtAppName.Width   = 220
$txtAppName.Margin  = New-Object System.Windows.Forms.Padding(0,2,10,0)

$btnAppSearch           = New-Object System.Windows.Forms.Button
$btnAppSearch.Text      = 'Find App Events'
$btnAppSearch.AutoSize  = $true
$btnAppSearch.Height    = 26
$btnAppSearch.Margin    = New-Object System.Windows.Forms.Padding(0,2,0,0)

foreach ($pair in @(
    @((New-Label 'Application:'), $txtAppName)
    @($btnAppSearch)
)) { foreach ($ctrl in $pair) { $filterRow3.Controls.Add($ctrl) } }

# Row 4: Security identity search (User / Host / IP)
$txtSecUser         = New-Object System.Windows.Forms.TextBox
$txtSecUser.Width   = 140
$txtSecUser.Margin  = New-Object System.Windows.Forms.Padding(0,2,10,0)

$txtSecHost         = New-Object System.Windows.Forms.TextBox
$txtSecHost.Width   = 140
$txtSecHost.Margin  = New-Object System.Windows.Forms.Padding(0,2,10,0)

$txtSecIP           = New-Object System.Windows.Forms.TextBox
$txtSecIP.Width     = 120
$txtSecIP.Margin    = New-Object System.Windows.Forms.Padding(0,2,10,0)

$btnSecSearch           = New-Object System.Windows.Forms.Button
$btnSecSearch.Text      = 'Search Security Events'
$btnSecSearch.AutoSize  = $true
$btnSecSearch.Height    = 26
$btnSecSearch.Margin    = New-Object System.Windows.Forms.Padding(0,2,0,0)

foreach ($pair in @(
    @((New-Label 'User:'), $txtSecUser)
    @((New-Label 'Host:'), $txtSecHost)
    @((New-Label 'IP:'),   $txtSecIP)
    @($btnSecSearch)
)) { foreach ($ctrl in $pair) { $filterRow4.Controls.Add($ctrl) } }

# Add rows to filter panel (reverse order since Dock=Top stacks bottom-up)
$pnlFilter.Controls.Add($filterRow4)
$pnlFilter.Controls.Add($filterRow3)
$pnlFilter.Controls.Add($filterRow2)
$pnlFilter.Controls.Add($filterRow1)
$rootTable.Controls.Add($pnlFilter, 0, 0)

# --- Presets Panel ---
# Fixed height with its own scrollbar. Under AutoSize, 36 wrapped preset buttons set the window's
# minimum height and consumed most of a small Backstage desktop.
$pnlPresets             = New-Object System.Windows.Forms.FlowLayoutPanel
$pnlPresets.Dock        = 'Fill'
$pnlPresets.AutoSize    = $false
$pnlPresets.AutoScroll  = $true
$pnlPresets.WrapContents= $true
$pnlPresets.Padding     = New-Object System.Windows.Forms.Padding(6,4,6,4)
$pnlPresets.BackColor   = [System.Drawing.Color]::FromArgb(238,238,245)

$lblPresets         = New-Object System.Windows.Forms.Label
$lblPresets.Text    = 'Quick Filters:'
$lblPresets.AutoSize= $true
$lblPresets.Font    = New-Object System.Drawing.Font('Segoe UI', 9, [System.Drawing.FontStyle]::Bold)
$lblPresets.Margin  = New-Object System.Windows.Forms.Padding(0,5,8,0)
$pnlPresets.Controls.Add($lblPresets)

function New-PresetButton {
    # One quick-filter button. Split out so Update-PresetButtons stays readable and so the hover
    # colours and click handler are defined in exactly one place.
    #
    # Over the 30-line guideline deliberately: this is a single object being configured, and the
    # three event handlers only make sense attached to the button they belong to. Splitting it
    # would mean passing the half-built control between functions for no gain in clarity.
    param([Parameter(Mandatory)] $Preset)

    $colour = if ($script:GroupColors.ContainsKey($Preset.Group)) {
        $script:GroupColors[$Preset.Group]
    } else {
        [System.Drawing.SystemColors]::Control
    }

    $button                = New-Object System.Windows.Forms.Button
    $button.Text           = $Preset.Label
    $button.AutoSize       = $true
    $button.FlatStyle      = 'Flat'
    $button.BackColor      = $colour
    $button.FlatAppearance.BorderColor = [System.Drawing.Color]::Silver
    $button.Margin         = New-Object System.Windows.Forms.Padding(2,3,2,3)
    $button.Tag            = $Preset
    if ($Preset.Description) { $toolTip.SetToolTip($button, $Preset.Description) }

    # Both take the button from $sender rather than $this. GetNewClosure captures the defining
    # scope, where $this does not exist, so inside the closure $this was $null and every
    # mouse-leave raised an error - once per button the pointer crossed.
    $button.Add_MouseEnter({
        param($sender, $eventArgs)
        $sender.BackColor = [System.Drawing.Color]::FromArgb(180,210,255)
    })
    $button.Add_MouseLeave({
        param($sender, $eventArgs)
        $data = $sender.Tag
        $sender.BackColor = if ($data -and $script:GroupColors.ContainsKey($data['Group'])) {
            $script:GroupColors[$data['Group']]
        } else {
            [System.Drawing.SystemColors]::Control
        }
    })
    # No GetNewClosure here either. It captures the variables of the defining scope, which froze
    # $cboLogSource to whatever it was at button-build time - under some hosting arrangements
    # that is $null, and the handler then fails on a property of nothing. Without it the control
    # is resolved when the button is actually clicked, which always works.
    $button.Add_Click({
        param($sender, $eventArgs)
        $data = $sender.Tag
        $cboLogSource.Text = $data['LogName']
        $txtEventId.Text   = ''
        Invoke-PresetSearch -Preset $data
    })

    return $button
}

function Update-PresetButtons {
    # Rebuilt rather than created once, so editing presets.json can take effect without
    # restarting. Only buttons are removed - the "Quick Filters:" label stays put.
    $pnlPresets.SuspendLayout()
    for ($i = $pnlPresets.Controls.Count - 1; $i -ge 0; $i--) {
        if ($pnlPresets.Controls[$i] -is [System.Windows.Forms.Button]) {
            $pnlPresets.Controls.RemoveAt($i)
        }
    }
    foreach ($preset in $script:Presets) {
        $pnlPresets.Controls.Add((New-PresetButton -Preset $preset))
    }
    $pnlPresets.ResumeLayout()
}

Update-PresetButtons
$rootTable.Controls.Add($pnlPresets, 0, 1)

# --- SplitContainer (results + detail) ---
$split                      = New-Object System.Windows.Forms.SplitContainer
$split.Dock                 = 'Fill'
$split.Orientation          = 'Horizontal'
$split.SplitterDistance     = 420
$split.Panel1MinSize        = 100
$split.Panel2MinSize        = 60
$rootTable.Controls.Add($split, 0, 2)

# Live filter bar above the grid
$pnlLiveFilter              = New-Object System.Windows.Forms.Panel
$pnlLiveFilter.Dock         = 'Top'
$pnlLiveFilter.Height       = 34
$pnlLiveFilter.Padding      = New-Object System.Windows.Forms.Padding(4,4,4,4)
$pnlLiveFilter.BackColor    = [System.Drawing.Color]::FromArgb(245,245,248)

$lblLive                    = New-Object System.Windows.Forms.Label
$lblLive.Text               = 'Filter results:'
$lblLive.AutoSize           = $true
$lblLive.Dock               = 'Left'
$lblLive.TextAlign          = 'MiddleLeft'
$lblLive.Margin             = New-Object System.Windows.Forms.Padding(0)

$txtLiveFilter              = New-Object System.Windows.Forms.TextBox
$txtLiveFilter.Dock         = 'Fill'
$txtLiveFilter.Font         = New-Object System.Drawing.Font('Segoe UI', 9)

$lblLiveCount               = New-Object System.Windows.Forms.Label
$lblLiveCount.Text          = ''
$lblLiveCount.Width         = 90
$lblLiveCount.Dock          = 'Right'
$lblLiveCount.TextAlign     = 'MiddleRight'
$lblLiveCount.ForeColor     = [System.Drawing.Color]::Gray

# Add right-to-left so Fill expands correctly
$pnlLiveFilter.Controls.Add($txtLiveFilter)
$pnlLiveFilter.Controls.Add($lblLiveCount)
$pnlLiveFilter.Controls.Add($lblLive)
# NOTE: not added to split.Panel1 yet - see comment below the DataGridView's Controls.Add call.

# DataGridView
$dgv                        = New-Object System.Windows.Forms.DataGridView
$dgv.Dock                   = 'Fill'
$dgv.ReadOnly               = $true
$dgv.SelectionMode          = 'FullRowSelect'
$dgv.MultiSelect            = $false
$dgv.AllowUserToAddRows     = $false
$dgv.RowHeadersVisible      = $false
$dgv.AutoGenerateColumns    = $false
$dgv.BackgroundColor        = [System.Drawing.Color]::White
$dgv.BorderStyle            = 'None'
$dgv.GridColor              = [System.Drawing.Color]::FromArgb(220,220,220)
$dgv.ColumnHeadersDefaultCellStyle.BackColor = [System.Drawing.Color]::FromArgb(230,230,235)
$dgv.EnableHeadersVisualStyles = $false
$dgv.ColumnHeadersVisible          = $true
$dgv.ColumnHeadersHeightSizeMode   = 'DisableResizing'
$dgv.ColumnHeadersHeight           = 28
$dgv.RowTemplate.Height             = 22
# Enable double-buffering via reflection to prevent flicker
$prop = $dgv.GetType().GetProperty('DoubleBuffered',
    [System.Reflection.BindingFlags]'Instance,NonPublic')
$prop.SetValue($dgv, $true, $null)

$colTime            = New-Object System.Windows.Forms.DataGridViewTextBoxColumn
$colTime.Name       = 'TimeCreated'
$colTime.HeaderText = 'Time'
$colTime.DataPropertyName = 'TimeCreated'
$colTime.Width      = 155
$colTime.DefaultCellStyle.Format = 'yyyy-MM-dd HH:mm:ss'

$colLevel           = New-Object System.Windows.Forms.DataGridViewTextBoxColumn
$colLevel.Name      = 'LevelDisplayName'
$colLevel.HeaderText= 'Level'
$colLevel.DataPropertyName = 'LevelDisplayName'
$colLevel.Width     = 90

$colSource          = New-Object System.Windows.Forms.DataGridViewTextBoxColumn
$colSource.Name     = 'ProviderName'
$colSource.HeaderText= 'Source'
$colSource.DataPropertyName = 'ProviderName'
$colSource.Width    = 200

$colId              = New-Object System.Windows.Forms.DataGridViewTextBoxColumn
$colId.Name         = 'Id'
$colId.HeaderText   = 'Event ID'
$colId.DataPropertyName = 'Id'
$colId.Width        = 75

$colMsg             = New-Object System.Windows.Forms.DataGridViewTextBoxColumn
$colMsg.Name        = 'Message'
$colMsg.HeaderText  = 'Message'
$colMsg.DataPropertyName = 'Message'
$colMsg.AutoSizeMode= 'Fill'

$colFull            = New-Object System.Windows.Forms.DataGridViewTextBoxColumn
$colFull.Name       = 'FullMessage'
$colFull.DataPropertyName = 'FullMessage'
$colFull.Visible    = $false

$null = $dgv.Columns.Add($colTime)
$null = $dgv.Columns.Add($colLevel)
$null = $dgv.Columns.Add($colSource)
$null = $dgv.Columns.Add($colId)
$null = $dgv.Columns.Add($colMsg)
$null = $dgv.Columns.Add($colFull)
$split.Panel1.Controls.Add($dgv)
# A Dock=Fill control must be added to its parent's Controls collection BEFORE a
# Dock=Top/Bottom/Left/Right sibling, or the Fill control's bounds end up spanning the
# entire parent (ignoring the other control's claimed space) regardless of Z-order,
# SuspendLayout/ResumeLayout, or a later PerformLayout() call - confirmed by isolated
# repro against both PowerShell-built and C#-compiled forms. dgv (Fill) is added above;
# pnlLiveFilter (Top) must come after it here for the layout to compute correctly.
$split.Panel1.Controls.Add($pnlLiveFilter)

# Detail pane
$txtDetail              = New-Object System.Windows.Forms.RichTextBox
$txtDetail.Dock         = 'Fill'
$txtDetail.ReadOnly     = $true
$txtDetail.Font         = New-Object System.Drawing.Font('Consolas', 9)
$txtDetail.BackColor    = [System.Drawing.Color]::FromArgb(250,250,252)
$txtDetail.BorderStyle  = 'None'
$txtDetail.ScrollBars   = 'Vertical'
$split.Panel2.Controls.Add($txtDetail)

# --- Status Strip ---
$status             = New-Object System.Windows.Forms.StatusStrip
$status.SizingGrip  = $true

$lblStatus          = New-Object System.Windows.Forms.ToolStripStatusLabel
$lblStatus.Text     = 'Ready'
$lblStatus.Spring   = $false

$lblCount           = New-Object System.Windows.Forms.ToolStripStatusLabel
$lblCount.Text      = ''
$lblCount.Spring    = $false
$lblCount.Alignment = 'Right'

$lblUpdate            = New-Object System.Windows.Forms.ToolStripStatusLabel
$lblUpdate.Text       = ''
$lblUpdate.Visible    = $false
$lblUpdate.IsLink     = $true
$lblUpdate.ForeColor  = [System.Drawing.Color]::FromArgb(0,102,204)
$lblUpdate.Add_Click({
    if (-not $script:LatestReleaseUrl) { return }

    if ($script:IsBackstage) {
        # Launching a browser as SYSTEM on an alternate desktop either silently does nothing or
        # starts one nobody can see, so hand over the link instead of pretending to open it.
        $message = "Release page:`n`n$($script:LatestReleaseUrl)"
        if (Set-ClipboardTextSafe $script:LatestReleaseUrl) {
            $message += "`n`nThe link has been copied to the clipboard on this machine."
        }
        [System.Windows.Forms.MessageBox]::Show(
            $message, 'Update available',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Information)
    } else {
        try {
            Start-Process $script:LatestReleaseUrl
        } catch {
            [System.Windows.Forms.MessageBox]::Show(
                "Could not open the link:`n$($script:LatestReleaseUrl)", 'Update available',
                [System.Windows.Forms.MessageBoxButtons]::OK,
                [System.Windows.Forms.MessageBoxIcon]::Warning)
        }
    }
})

# Which host mode the app decided it is in. Shown deliberately: when a fallback takes effect over
# a remote session, this is what makes the behaviour explicable rather than mysterious.
$lblEnvironment           = New-Object System.Windows.Forms.ToolStripStatusLabel
$lblEnvironment.Text      = Get-HostDescription
$lblEnvironment.ForeColor = [System.Drawing.Color]::Gray
$lblEnvironment.Alignment = 'Right'

$spacer             = New-Object System.Windows.Forms.ToolStripStatusLabel
$spacer.Spring      = $true

$progressBar                = New-Object System.Windows.Forms.ToolStripProgressBar
$progressBar.Style          = 'Marquee'
$progressBar.MarqueeAnimationSpeed = 30
$progressBar.Width          = 160
$progressBar.Visible        = $false

$null = $status.Items.Add($lblStatus)
$null = $status.Items.Add($lblUpdate)
$null = $status.Items.Add($spacer)
$null = $status.Items.Add($lblCount)
$null = $status.Items.Add($progressBar)
$null = $status.Items.Add($lblEnvironment)
$rootTable.Controls.Add($status, 0, 3)
#endregion

#region 6 - Preset Editor

# A window over the same presets.json the merge in Region 3 reads. Everything here is editing an
# in-memory copy: nothing touches the live preset set or the file until Save, so Cancel is a real
# cancel. Hand-editing the JSON still works and is still supported - this is a faster front end
# to it, not a replacement format.

$script:EditorIndex   = -1
$script:EditorLoading = $false

function Show-EditorProblem {
    param([string] $Message)
    [void][System.Windows.Forms.MessageBox]::Show(
        $Message, 'Presets',
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Warning)
}

function New-EditorListItem {
    param($Row)
    $item = New-Object System.Windows.Forms.ListViewItem($Row.Preset['Label'])
    $null = $item.SubItems.Add([string]$Row.Preset['Group'])
    $null = $item.SubItems.Add((Get-EditorRowStatus $Row))
    if ($Row.Hidden) { $item.ForeColor = [System.Drawing.Color]::Gray }
    return $item
}

function Update-EditorRow {
    param([int] $Index)
    if ($Index -lt 0 -or $Index -ge $script:EdList.Items.Count) { return }

    $row  = $script:EditorRows[$Index]
    $item = $script:EdList.Items[$Index]
    $item.Text             = $row.Preset['Label']
    $item.SubItems[1].Text = [string]$row.Preset['Group']
    $item.SubItems[2].Text = Get-EditorRowStatus $row
    $item.ForeColor        = if ($row.Hidden) {
        [System.Drawing.Color]::Gray
    } else {
        [System.Drawing.SystemColors]::WindowText
    }
}

function Update-EditorList {
    # Rebuilds the list and moves the selection itself rather than letting the ListView's own
    # change event do it, so adding or deleting a preset lands on the right row exactly once.
    param([int] $Select = -1)

    $script:EditorLoading = $true
    try {
        $script:EdList.BeginUpdate()
        $script:EdList.Items.Clear()
        foreach ($row in $script:EditorRows) { $null = $script:EdList.Items.Add((New-EditorListItem $row)) }
        $script:EdList.EndUpdate()

        if ($Select -ge 0 -and $Select -lt $script:EdList.Items.Count) {
            $script:EdList.Items[$Select].Selected = $true
            $script:EdList.Items[$Select].EnsureVisible()
            $script:EditorIndex = $Select
        } else {
            $script:EditorIndex = -1
        }
    } finally {
        $script:EditorLoading = $false
    }
    Show-EditorFields $script:EditorIndex
}

function Show-EditorFields {
    # Loads one row into the fields. Guarded by EditorLoading throughout: every field raises a
    # change event as it is populated, and without the guard those would write straight back
    # into the row that is being displayed.
    param([int] $Index)

    $script:EditorLoading = $true
    try {
        $fields = @($script:EdGroup, $script:EdLabel, $script:EdLog, $script:EdIds,
                    $script:EdProviders, $script:EdFilter, $script:EdLog2, $script:EdIds2,
                    $script:EdDescription)
        foreach ($field in $fields) { $field.Enabled = ($Index -ge 0) }

        if ($Index -lt 0) {
            foreach ($field in $fields) { $field.Text = '' }
            $script:EdHidden.Enabled = $false
            $script:EdDelete.Enabled = $false
            $script:EdClone.Enabled  = $false
            $script:EdTest.Enabled   = $false
            return
        }

        $row    = $script:EditorRows[$Index]
        $preset = $row.Preset
        $script:EdGroup.Text       = [string]$preset['Group']
        $script:EdLabel.Text       = [string]$preset['Label']
        $script:EdLog.Text         = [string]$preset['LogName']
        $script:EdIds.Text         = Format-PresetList $preset['Id']
        $script:EdProviders.Text   = Format-PresetList $preset['ProviderName']
        $script:EdFilter.Text      = [string]$preset['MessageFilter']
        $script:EdLog2.Text        = [string]$preset['LogName2']
        $script:EdIds2.Text        = Format-PresetList $preset['Id2']
        $script:EdDescription.Text = [string]$preset['Description']
        $script:EdHidden.Checked   = $row.Hidden

        # Hiding only means something for a built-in - it is how you switch one off without
        # losing it. A custom preset has nothing to fall back to, so Delete is the operation
        # that applies, and exactly one of the two is offered at a time.
        $isBuiltIn = [bool](Get-BuiltInPreset $preset['Label'])
        $script:EdHidden.Enabled = $isBuiltIn
        $script:EdDelete.Enabled = -not $isBuiltIn
        $script:EdClone.Enabled  = $true
        $script:EdTest.Enabled   = $true
    } finally {
        $script:EditorLoading = $false
    }
}

function Test-EditorFieldInput {
    # Validation only. Split from the write below so that a rejected edit changes nothing at
    # all - a half-applied preset would be worse than a refused one.
    param([hashtable] $Values)

    if (-not $Values.Label) { Show-EditorProblem 'A preset needs a name.'; return $false }
    if (-not $Values.Log)   { Show-EditorProblem 'A preset needs a log to search.'; return $false }

    for ($i = 0; $i -lt $script:EditorRows.Count; $i++) {
        if ($i -eq $script:EditorIndex) { continue }
        if ($script:EditorRows[$i].Preset['Label'] -eq $Values.Label) {
            Show-EditorProblem ("Another preset is already called '$($Values.Label)'. Names are " +
                'how presets.json identifies a preset, so they have to be unique.')
            return $false
        }
    }

    if ($Values.Log2 -and $Values.Ids2.Count -eq 0) {
        Show-EditorProblem ('A second log needs its own Event IDs. Without them the whole of ' +
            "'$($Values.Log2)' would be returned alongside the first log.")
        return $false
    }
    return $true
}

function Save-EditorFields {
    # Writes the fields back into the selected row. Returns $false, having said why, if anything
    # is invalid - so a mistyped Event ID stops the save rather than being silently dropped.
    if ($script:EditorIndex -lt 0) { return $true }

    try {
        $values = @{
            Label     = $script:EdLabel.Text.Trim()
            Log       = $script:EdLog.Text.Trim()
            Log2      = $script:EdLog2.Text.Trim()
            Ids       = Read-IdList $script:EdIds.Text  'Event IDs'
            Ids2      = Read-IdList $script:EdIds2.Text 'Second log Event IDs'
            Providers = Read-NameList $script:EdProviders.Text
        }
    } catch {
        Show-EditorProblem $_.Exception.Message
        return $false
    }

    if (-not (Test-EditorFieldInput -Values $values)) { return $false }

    $row    = $script:EditorRows[$script:EditorIndex]
    $preset = $row.Preset
    $group  = $script:EdGroup.Text.Trim()

    $preset['Group']         = if ($group) { $group } else { 'Custom' }
    $preset['Label']         = $values.Label
    $preset['LogName']       = $values.Log
    $preset['Id']            = $values.Ids
    $preset['ProviderName']  = $values.Providers
    $preset['MessageFilter'] = $script:EdFilter.Text.Trim()
    $preset['Description']   = $script:EdDescription.Text.Trim()

    if ($values.Log2) {
        $preset['LogName2'] = $values.Log2
        $preset['Id2']      = $values.Ids2
    } else {
        $preset.Remove('LogName2')
        $preset.Remove('Id2')
    }

    $row.Hidden = $script:EdHidden.Checked
    Update-EditorRow $script:EditorIndex
    return $true
}

function Add-EditorPreset {
    param($Preset, [Parameter(Mandatory)] [string] $Label)

    if (-not (Save-EditorFields)) { return }
    $new = Copy-Preset $Preset
    $new['Label'] = New-UniqueEditorLabel -Base $Label -Rows $script:EditorRows
    $null = $script:EditorRows.Add((New-EditorRow $new $false))
    Update-EditorList -Select ($script:EditorRows.Count - 1)
    $script:EdLabel.Focus()
    $script:EdLabel.SelectAll()
}

function Remove-EditorPreset {
    if ($script:EditorIndex -lt 0) { return }
    $row = $script:EditorRows[$script:EditorIndex]

    # A built-in is never removed from the list, only hidden - deleting it would take away the
    # one place it could be switched back on.
    if (Get-BuiltInPreset $row.Preset['Label']) { return }

    $answer = [System.Windows.Forms.MessageBox]::Show(
        "Delete the custom preset '$($row.Preset['Label'])'?", 'Presets',
        [System.Windows.Forms.MessageBoxButtons]::YesNo,
        [System.Windows.Forms.MessageBoxIcon]::Question)
    if ($answer -ne [System.Windows.Forms.DialogResult]::Yes) { return }

    $index = $script:EditorIndex
    $script:EditorRows.RemoveAt($index)
    Update-EditorList -Select ([Math]::Min($index, $script:EditorRows.Count - 1))
}

function Invoke-EditorTest {
    # Runs the preset as edited, against this machine, right now. Catching a wrong Event ID here
    # takes seconds; catching it by wondering why a button returns nothing takes a lot longer.
    if (-not (Save-EditorFields)) { return }
    if ($script:EditorIndex -lt 0) { return }

    $preset  = $script:EditorRows[$script:EditorIndex].Preset
    $sample  = 50
    $script:EdForm.Cursor = [System.Windows.Forms.Cursors]::WaitCursor
    $script:EdStatus.Text = "Testing '$($preset['Label'])'..."
    $script:EdStatus.Refresh()

    try {
        $found = @(Get-EventsForPreset -Preset $preset -MaxEvents $sample)
        $script:EdStatus.Text = if ($found.Count -eq 0) {
            'No matches on this machine. The log may be empty here, or the Event IDs may be wrong.'
        } elseif ($found.Count -ge $sample) {
            "$sample+ matches - most recent $($found[0].TimeCreated)."
        } else {
            "$($found.Count) match(es) - most recent $($found[0].TimeCreated)."
        }
    } catch [System.UnauthorizedAccessException] {
        $script:EdStatus.Text = 'Access denied - this log needs Administrator rights.'
    } catch {
        $script:EdStatus.Text = "Test failed: $(($_.Exception.Message -split [Environment]::NewLine)[0])"
    } finally {
        $script:EdForm.Cursor = [System.Windows.Forms.Cursors]::Default
    }
}

function Invoke-EditorSave {
    if (-not (Save-EditorFields)) { return $false }

    $kept = @($script:EditorRows | Where-Object { -not $_.Hidden } | ForEach-Object { $_.Preset })
    try {
        $null = Export-PresetOverrides -Presets $kept
    } catch {
        Show-EditorProblem "Could not write the preset file:$([Environment]::NewLine)$($script:PresetFilePath)$([Environment]::NewLine)$([Environment]::NewLine)$($_.Exception.Message)"
        return $false
    }

    # Reload from the file just written rather than trusting the in-memory set. This puts every
    # save through exactly the same merge a cold start would, so a writer bug shows up here,
    # while the editor is still open, instead of silently on someone else's next launch.
    Import-PresetOverrides
    Update-PresetButtons
    return $true
}

function New-EditorTextBox {
    param([bool] $Multiline = $false)
    $box           = New-Object System.Windows.Forms.TextBox
    $box.Anchor    = 'Left,Right'
    $box.Multiline = $Multiline
    if ($Multiline) {
        $box.Anchor     = 'Left,Right,Top,Bottom'
        $box.ScrollBars = 'Vertical'
    }
    return $box
}

function New-EditorCombo {
    param([string[]] $Items)
    $combo               = New-Object System.Windows.Forms.ComboBox
    $combo.DropDownStyle = 'DropDown'
    $combo.Anchor        = 'Left,Right'
    $combo.AutoCompleteMode   = 'SuggestAppend'
    $combo.AutoCompleteSource = 'ListItems'
    foreach ($item in $Items) { $null = $combo.Items.Add($item) }
    return $combo
}

function Add-EditorField {
    param($Table, [int] $Row, [string] $Caption, $Control, [string] $Hint)

    # Not named $caption: PowerShell variable names are case-insensitive, so it would be the
    # same variable as the [string] $Caption parameter and the label would be coerced to text.
    $captionLabel          = New-Object System.Windows.Forms.Label
    $captionLabel.Text     = $Caption
    $captionLabel.AutoSize = $true
    $captionLabel.Anchor   = 'Left'
    $captionLabel.Margin   = New-Object System.Windows.Forms.Padding(0, 7, 8, 0)
    $Table.Controls.Add($captionLabel, 0, $Row)

    $Control.Margin = New-Object System.Windows.Forms.Padding(0, 3, 0, 3)
    $Table.Controls.Add($Control, 1, $Row)
    if ($Hint) { $script:EdToolTip.SetToolTip($Control, $Hint) }
}

function New-EditorButton {
    param([string] $Text, [int] $Width = 84)
    $button        = New-Object System.Windows.Forms.Button
    $button.Text   = $Text
    $button.Width  = $Width
    $button.Height = 26
    $button.Margin = New-Object System.Windows.Forms.Padding(4, 0, 0, 0)
    return $button
}

function New-EditorDetailTable {
    # Nine labelled fields, with Description taking whatever height is left over. Built here
    # rather than inline in Show-PresetEditor to keep that function to assembly and wiring.
    $groups = @(@($script:BuiltInPresets | ForEach-Object { $_['Group'] } | Sort-Object -Unique) + 'Custom' |
                Sort-Object -Unique)

    $script:EdGroup       = New-EditorCombo -Items $groups
    $script:EdLabel       = New-EditorTextBox
    $script:EdLog         = New-EditorCombo -Items $script:LogSources
    $script:EdIds         = New-EditorTextBox
    $script:EdProviders   = New-EditorTextBox
    $script:EdFilter      = New-EditorTextBox
    $script:EdLog2        = New-EditorCombo -Items $script:LogSources
    $script:EdIds2        = New-EditorTextBox
    $script:EdDescription = New-EditorTextBox -Multiline $true

    $table             = New-Object System.Windows.Forms.TableLayoutPanel
    $table.Dock        = 'Fill'
    $table.ColumnCount = 2
    $table.RowCount    = 9
    $table.Padding     = New-Object System.Windows.Forms.Padding(10, 0, 0, 0)
    $null = $table.ColumnStyles.Add((New-Object System.Windows.Forms.ColumnStyle([System.Windows.Forms.SizeType]::AutoSize)))
    $null = $table.ColumnStyles.Add((New-Object System.Windows.Forms.ColumnStyle([System.Windows.Forms.SizeType]::Percent, 100)))
    for ($i = 0; $i -lt 8; $i++) {
        $null = $table.RowStyles.Add((New-Object System.Windows.Forms.RowStyle([System.Windows.Forms.SizeType]::AutoSize)))
    }
    $null = $table.RowStyles.Add((New-Object System.Windows.Forms.RowStyle([System.Windows.Forms.SizeType]::Percent, 100)))

    Add-EditorField $table 0 'Group'       $script:EdGroup       'Sets the button colour and groups related presets together.'
    Add-EditorField $table 1 'Name'        $script:EdLabel       'The button text, and the name presets.json uses to identify this preset.'
    Add-EditorField $table 2 'Log'         $script:EdLog         'The event log to search, e.g. System, Application, Security.'
    Add-EditorField $table 3 'Event IDs'   $script:EdIds         'Comma-separated, e.g. 4624, 4625. Leave empty to return the whole log.'
    Add-EditorField $table 4 'Providers'   $script:EdProviders   'Comma-separated. Narrows a widely reused Event ID to the source that means what you want.'
    Add-EditorField $table 5 'Message has' $script:EdFilter      'Keeps only events whose message contains this text. For IDs shared across services, such as 7045.'
    Add-EditorField $table 6 'Second log'  $script:EdLog2        'Optional. Searches a second log at the same time and merges the results.'
    Add-EditorField $table 7 'Its IDs'     $script:EdIds2        'Event IDs for the second log. Required if a second log is set.'
    Add-EditorField $table 8 'Description' $script:EdDescription 'Shown as the tooltip on the preset button.'

    return $table
}

function New-EditorListPanel {
    $script:EdList              = New-Object System.Windows.Forms.ListView
    $script:EdList.Dock         = 'Fill'
    $script:EdList.View         = 'Details'
    $script:EdList.FullRowSelect = $true
    $script:EdList.MultiSelect  = $false
    $script:EdList.HideSelection = $false
    $null = $script:EdList.Columns.Add('Preset', 150)
    $null = $script:EdList.Columns.Add('Group', 105)
    $null = $script:EdList.Columns.Add('Status', 70)

    $script:EdNew    = New-EditorButton 'New'
    $script:EdClone  = New-EditorButton 'Clone'
    $script:EdDelete = New-EditorButton 'Delete'

    $buttons             = New-Object System.Windows.Forms.FlowLayoutPanel
    $buttons.Dock        = 'Bottom'
    $buttons.Height      = 34
    $buttons.Padding     = New-Object System.Windows.Forms.Padding(0, 5, 0, 0)
    $buttons.WrapContents = $false
    foreach ($button in @($script:EdNew, $script:EdClone, $script:EdDelete)) {
        $button.Margin = New-Object System.Windows.Forms.Padding(0, 0, 4, 0)
        $null = $buttons.Controls.Add($button)
    }

    $panel        = New-Object System.Windows.Forms.Panel
    $panel.Dock   = 'Fill'
    $panel.Margin = New-Object System.Windows.Forms.Padding(0)
    $null = $panel.Controls.Add($script:EdList)
    $null = $panel.Controls.Add($buttons)
    return $panel
}

function New-EditorFooter {
    $script:EdTest   = New-EditorButton 'Test'
    $script:EdSave   = New-EditorButton 'Save' 90
    $script:EdCancel = New-EditorButton 'Cancel'

    $script:EdStatus           = New-Object System.Windows.Forms.Label
    $script:EdStatus.Dock      = 'Fill'
    $script:EdStatus.TextAlign = 'MiddleLeft'
    $script:EdStatus.ForeColor = [System.Drawing.Color]::FromArgb(70, 70, 70)
    $script:EdStatus.AutoEllipsis = $true

    $right              = New-Object System.Windows.Forms.FlowLayoutPanel
    $right.Dock         = 'Right'
    $right.FlowDirection = 'RightToLeft'
    $right.WrapContents = $false
    $right.AutoSize     = $true
    foreach ($button in @($script:EdCancel, $script:EdSave, $script:EdTest)) {
        $null = $right.Controls.Add($button)
    }

    $footer         = New-Object System.Windows.Forms.Panel
    $footer.Dock    = 'Fill'
    $footer.Padding = New-Object System.Windows.Forms.Padding(0, 8, 0, 0)
    $null = $footer.Controls.Add($script:EdStatus)
    $null = $footer.Controls.Add($right)
    return $footer
}

function Register-EditorHandlers {
    $script:EdList.Add_SelectedIndexChanged({
        if ($script:EditorLoading) { return }
        $selected = if ($script:EdList.SelectedIndices.Count -gt 0) { $script:EdList.SelectedIndices[0] } else { -1 }
        if ($selected -lt 0 -or $selected -eq $script:EditorIndex) { return }

        # Commit before moving away, so clicking straight to another preset keeps the edit
        # rather than discarding it. A rejected edit puts the selection back where it was.
        if (-not (Save-EditorFields)) {
            $script:EditorLoading = $true
            try { $script:EdList.Items[$script:EditorIndex].Selected = $true } finally { $script:EditorLoading = $false }
            return
        }
        $script:EditorIndex = $selected
        Show-EditorFields $selected
    })

    # Live, so the list reflects a rename as it is typed. Display only - the row itself is not
    # written until the edit is committed.
    $script:EdLabel.Add_TextChanged({
        if ($script:EditorLoading -or $script:EditorIndex -lt 0) { return }
        $script:EdList.Items[$script:EditorIndex].Text = $script:EdLabel.Text
    })

    $script:EdHidden.Add_CheckedChanged({
        if ($script:EditorLoading -or $script:EditorIndex -lt 0) { return }
        $script:EditorRows[$script:EditorIndex].Hidden = $script:EdHidden.Checked
        Update-EditorRow $script:EditorIndex
    })

    $script:EdNew.Add_Click({
        Add-EditorPreset -Preset ([ordered]@{ Group = 'Custom'; LogName = 'Application'; Description = '' }) -Label 'New Preset'
    })
    $script:EdClone.Add_Click({
        if ($script:EditorIndex -lt 0) { return }
        $source = $script:EditorRows[$script:EditorIndex].Preset
        Add-EditorPreset -Preset $source -Label "$($source['Label']) copy"
    })
    $script:EdDelete.Add_Click({ Remove-EditorPreset })
    $script:EdTest.Add_Click({ Invoke-EditorTest })

    $script:EdSave.Add_Click({
        if (Invoke-EditorSave) {
            $script:EdForm.DialogResult = [System.Windows.Forms.DialogResult]::OK
            $script:EdForm.Close()
        }
    })
    $script:EdCancel.Add_Click({
        $script:EdForm.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
        $script:EdForm.Close()
    })
}

function Show-PresetEditor {
    # Assembly and wiring only; each piece is built by one of the functions above. Returns the
    # dialog result so the caller can tell a save from a cancel.
    $script:EditorRows  = New-EditorRowList
    $script:EditorIndex = -1
    $script:EdToolTip   = New-Object System.Windows.Forms.ToolTip

    $script:EdHidden          = New-Object System.Windows.Forms.CheckBox
    $script:EdHidden.Text     = 'Hide this preset'
    $script:EdHidden.AutoSize = $true
    $script:EdHidden.Margin   = New-Object System.Windows.Forms.Padding(10, 6, 0, 0)
    $script:EdToolTip.SetToolTip($script:EdHidden,
        'Switches a built-in preset off without losing it. Clear this box to bring it back.')

    # Sized against the desktop it lands on, for the same reason the main window is: a
    # ScreenConnect Backstage desktop is commonly 1024x768 and can be smaller.
    $area   = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $width  = [int][Math]::Min(920, [Math]::Max(660, $area.Width  - 60))
    $height = [int][Math]::Min(600, [Math]::Max(460, $area.Height - 60))

    $script:EdForm                 = New-Object System.Windows.Forms.Form
    $script:EdForm.Text            = 'Winnow - Presets'
    $script:EdForm.Size            = New-Object System.Drawing.Size($width, $height)
    $script:EdForm.MinimumSize     = New-Object System.Drawing.Size(660, 460)
    $script:EdForm.StartPosition   = 'CenterParent'
    $script:EdForm.MinimizeBox     = $false
    $script:EdForm.Font            = New-Object System.Drawing.Font('Segoe UI', 9)
    $script:EdForm.Padding         = New-Object System.Windows.Forms.Padding(10)
    # No AcceptButton: Enter belongs to the field being typed in, not to Save.

    $root             = New-Object System.Windows.Forms.TableLayoutPanel
    $root.Dock        = 'Fill'
    $root.ColumnCount = 2
    $root.RowCount    = 3
    $null = $root.ColumnStyles.Add((New-Object System.Windows.Forms.ColumnStyle([System.Windows.Forms.SizeType]::Absolute, 340)))
    $null = $root.ColumnStyles.Add((New-Object System.Windows.Forms.ColumnStyle([System.Windows.Forms.SizeType]::Percent, 100)))
    $null = $root.RowStyles.Add((New-Object System.Windows.Forms.RowStyle([System.Windows.Forms.SizeType]::Percent, 100)))
    $null = $root.RowStyles.Add((New-Object System.Windows.Forms.RowStyle([System.Windows.Forms.SizeType]::AutoSize)))
    $null = $root.RowStyles.Add((New-Object System.Windows.Forms.RowStyle([System.Windows.Forms.SizeType]::AutoSize)))

    $root.Controls.Add((New-EditorListPanel), 0, 0)
    $root.Controls.Add((New-EditorDetailTable), 1, 0)
    $root.Controls.Add($script:EdHidden, 1, 1)

    $footer = New-EditorFooter
    $root.SetColumnSpan($footer, 2)
    $root.Controls.Add($footer, 0, 2)
    $script:EdForm.Controls.Add($root)

    # Wired after every control exists, so CancelButton and the handlers refer to real objects.
    $script:EdForm.CancelButton = $script:EdCancel
    Register-EditorHandlers

    $first = if ($script:EditorRows.Count -gt 0) { 0 } else { -1 }
    Update-EditorList -Select $first
    $script:EdStatus.Text = "Editing $($script:PresetFilePath)"

    return $script:EdForm.ShowDialog($mainForm)
}

#endregion

#region 7 - Event Wiring and Search Logic

function Set-Searching([bool]$active) {
    $btnSearch.Enabled      = -not $active
    $btnClear.Enabled       = -not $active
    $progressBar.Visible    = $active
    if ($active) {
        $lblStatus.Text = 'Searching...'
        $lblCount.Text  = ''
    }
}

function Invoke-Search {
    $logName = $cboLogSource.Text.Trim()
    if (-not $logName) {
        [System.Windows.Forms.MessageBox]::Show('Please select or enter a Log Source.', 'Validation',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Warning)
        return
    }

    # Elevation warning for Security log
    if ($logName -eq 'Security' -and -not $script:isAdmin) {
        $r = [System.Windows.Forms.MessageBox]::Show(
            "The Security log requires Administrator privileges.`n`nContinue anyway (query will likely fail)?",
            'Elevation Required',
            [System.Windows.Forms.MessageBoxButtons]::YesNo,
            [System.Windows.Forms.MessageBoxIcon]::Warning)
        if ($r -eq [System.Windows.Forms.DialogResult]::No) { return }
    }

    # Build filter hash
    $fh = @{ LogName = $logName }

    $levelVal = $script:LevelMap[$cboLevel.SelectedItem.ToString()]
    if ($null -ne $levelVal) { $fh['Level'] = $levelVal }

    $idText = $txtEventId.Text.Trim()
    if ($idText) {
        try {
            $ids = $idText -split '\s*,\s*' | Where-Object { $_ -ne '' } | ForEach-Object { [int]$_ }
            if ($ids.Count -gt 0) { $fh['Id'] = [int[]]$ids }
        } catch {
            [System.Windows.Forms.MessageBox]::Show("Invalid Event ID: '$idText'`nUse comma-separated integers.", 'Validation',
                [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Warning)
            return
        }
    }

    if ($dtpFrom.Checked) { $fh['StartTime'] = $dtpFrom.Value }
    if ($dtpTo.Checked)   { $fh['EndTime']   = $dtpTo.Value }

    Set-Searching $true

    Invoke-QuerySync (New-QueryArgument -FilterHash $fh `
        -MaxEvents ([int]$nudMaxEvents.Value) -Keyword $txtKeyword.Text.Trim())
}

function Invoke-PresetSearch {
    param($Preset)
    if ($Preset.LogName -eq 'Security' -and -not $script:isAdmin) {
        $r = [System.Windows.Forms.MessageBox]::Show(
            "The Security log requires Administrator privileges.`n`nContinue anyway?",
            'Elevation Required',
            [System.Windows.Forms.MessageBoxButtons]::YesNo,
            [System.Windows.Forms.MessageBoxIcon]::Warning)
        if ($r -eq [System.Windows.Forms.DialogResult]::No) { return }
    }

    Set-Searching $true
    Invoke-QuerySync (New-QueryArgument -Preset $Preset `
        -MaxEvents ([int]$nudMaxEvents.Value) -Keyword $txtKeyword.Text.Trim())
}

function Invoke-AppSearch {
    $app = $txtAppName.Text.Trim()
    if (-not $app) {
        [System.Windows.Forms.MessageBox]::Show('Please enter an application name.', 'Validation',
            [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Warning)
        return
    }
    Set-Searching $true
    Invoke-QuerySync (New-QueryArgument -AppName $app `
        -MaxEvents ([int]$nudMaxEvents.Value) -Keyword $txtKeyword.Text.Trim())
}

function Invoke-SecurityIdentitySearch {
    $u  = $txtSecUser.Text.Trim()
    $h  = $txtSecHost.Text.Trim()
    $ip = $txtSecIP.Text.Trim()
    if (-not $u -and -not $h -and -not $ip) {
        [System.Windows.Forms.MessageBox]::Show('Enter at least one of User, Host, or IP.', 'Validation',
            [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Warning)
        return
    }
    if (-not $script:isAdmin) {
        $r = [System.Windows.Forms.MessageBox]::Show(
            "The Security log requires Administrator privileges.`n`nContinue anyway (query will likely fail)?",
            'Elevation Required',
            [System.Windows.Forms.MessageBoxButtons]::YesNo,
            [System.Windows.Forms.MessageBoxIcon]::Warning)
        if ($r -eq [System.Windows.Forms.DialogResult]::No) { return }
    }
    Set-Searching $true
    Invoke-QuerySync (New-QueryArgument -SecurityIdentity @{ UserName = $u; HostName = $h; IPAddress = $ip } `
        -MaxEvents ([int]$nudMaxEvents.Value) -Keyword $txtKeyword.Text.Trim())
}

function Invoke-ClearFilters {
    $cboLogSource.SelectedIndex = 0
    $cboLevel.SelectedIndex     = 0
    $txtEventId.Text            = ''
    $txtKeyword.Text            = ''
    $dtpFrom.Checked            = $false
    $dtpTo.Checked              = $false
    $nudMaxEvents.Value         = 1000
    $txtAppName.Text            = ''
    $txtSecUser.Text            = ''
    $txtSecHost.Text            = ''
    $txtSecIP.Text              = ''
    $dgv.DataSource             = $null
    $txtLiveFilter.Text         = ''
    $lblLiveCount.Text          = ''
    $txtDetail.Text             = ''
    $lblStatus.Text             = 'Ready'
    $lblCount.Text              = ''
    $btnExport.Enabled          = $false
    $script:currentResults      = $null
}

# Show-SearchResults / Invoke-QuerySync (wired here because they reference UI controls)
function Show-SearchResults {
    param($Result)

    if ($Result -is [hashtable] -and $Result.ContainsKey('Error')) {
        $lblStatus.Text = 'Error'
        [System.Windows.Forms.MessageBox]::Show($Result.Error, 'Search Error',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error)
        return
    }

    $events  = if ($Result.ContainsKey('Events') -and $Result.Events) { [object[]]$Result.Events } else { @() }
    $keyword = if ($Result.ContainsKey('Keyword')) { $Result.Keyword } else { '' }

    $dt = Build-DataTable -Events $events -Keyword $keyword

    $script:currentResults = $events

    $dgv.DataSource = $null
    $dgv.DataSource = $dt

    $count = $dt.Rows.Count
    if ($count -eq 0) {
        $lblStatus.Text = '0 records found — try widening filters'
        $lblCount.Text  = ''
        $btnExport.Enabled = $false
    } else {
        $lblStatus.Text    = 'Done'
        $lblCount.Text     = "$count record(s)"
        $btnExport.Enabled = $true
    }

    $txtLiveFilter.Text = ''
    $lblLiveCount.Text  = ''
    $txtDetail.Text     = ''
    if ($dgv.Rows.Count -gt 0) {
        $dgv.FirstDisplayedScrollingRowIndex = 0
        $dgv.CurrentCell = $dgv.Rows[0].Cells[0]
    }
}

$script:isSearching = $false

function Invoke-QuerySync {
    param($WorkerArgs)
    if ($script:isSearching) { return }
    $script:isSearching = $true
    try {
        Set-Searching $true
        [System.Windows.Forms.Application]::DoEvents()
        $result = Invoke-EventQuery -Argument $WorkerArgs
        Set-Searching $false
        Show-SearchResults $result
    } finally {
        $script:isSearching = $false
    }
}

$txtLiveFilter.Add_TextChanged({
    $dt = $dgv.DataSource
    if (-not $dt) { return }
    $term = $txtLiveFilter.Text.Trim()
    if ($term -eq '') {
        $dt.DefaultView.RowFilter = ''
    } else {
        # Escape single quotes for DataView filter syntax
        $safe = $term.Replace("'", "''")
        $dt.DefaultView.RowFilter = "Message LIKE '*$safe*' OR ProviderName LIKE '*$safe*' OR CONVERT(Id, System.String) LIKE '*$safe*'"
    }
    $lblLiveCount.Text = if ($term) { "$($dt.DefaultView.Count) shown" } else { '' }
    $txtDetail.Text    = ''
})

$btnSearch.Add_Click({ Invoke-Search })

$btnAppSearch.Add_Click({ Invoke-AppSearch })

$btnSecSearch.Add_Click({ Invoke-SecurityIdentitySearch })

$btnClear.Add_Click({ Invoke-ClearFilters })

$btnExport.Add_Click({
    if ($script:currentResults) { Invoke-Export -Results $script:currentResults }
})

$btnPresets.Add_Click({
    # The editor saves and reloads the preset strip itself, so there is nothing to do on cancel.
    $result = Show-PresetEditor
    if ($result -ne [System.Windows.Forms.DialogResult]::OK) { return }

    $lblStatus.Text = if ($script:PresetWarning) {
        $script:PresetWarning
    } else {
        "Presets saved - $($script:Presets.Count) available"
    }
})

$dgv.Add_SelectionChanged({
    if ($dgv.SelectedRows.Count -eq 0) { return }
    $item = $dgv.SelectedRows[0].DataBoundItem
    if ($item) { $txtDetail.Text = $item['FullMessage'] }
})

# Enter key triggers search from filter controls
$searchOnEnter = {
    param($s,$e)
    if ($e.KeyCode -eq [System.Windows.Forms.Keys]::Return) {
        Invoke-Search
        $e.SuppressKeyPress = $true
    }
}
$cboLogSource.Add_KeyDown($searchOnEnter)
$txtEventId.Add_KeyDown($searchOnEnter)
$txtKeyword.Add_KeyDown($searchOnEnter)

$appSearchOnEnter = {
    param($s,$e)
    if ($e.KeyCode -eq [System.Windows.Forms.Keys]::Return) {
        Invoke-AppSearch
        $e.SuppressKeyPress = $true
    }
}
$txtAppName.Add_KeyDown($appSearchOnEnter)

$secSearchOnEnter = {
    param($s,$e)
    if ($e.KeyCode -eq [System.Windows.Forms.Keys]::Return) {
        Invoke-SecurityIdentitySearch
        $e.SuppressKeyPress = $true
    }
}
$txtSecUser.Add_KeyDown($secSearchOnEnter)
$txtSecHost.Add_KeyDown($secSearchOnEnter)
$txtSecIP.Add_KeyDown($secSearchOnEnter)

$mainForm.AcceptButton = $btnSearch
#endregion

#region 8 - Launch

# One-shot, deferred update check so it never delays the window showing up. Runs on the UI
# thread (not a background thread/job - see Region 4's note on why that's unreliable here),
# with a short request timeout so a slow/blocked network can only ever cost a few seconds,
# well after the window is already visible and usable.
$updateTimer = New-Object System.Windows.Forms.Timer
$updateTimer.Interval = 1500
$updateTimer.Add_Tick({
    $updateTimer.Stop()
    $newVersion = Test-ForUpdate
    if ($newVersion) {
        $action = if ($script:IsBackstage) { 'click to copy link' } else { 'click to download' }
        $lblUpdate.Text    = "Update available: $newVersion ($action)"
        $lblUpdate.Visible = $true
    }
})

$mainForm.Add_Shown({
    $cboLogSource.Focus()
    # Surfaced here rather than at load: presets are merged before the status bar exists.
    if ($script:PresetWarning) { $lblStatus.Text = $script:PresetWarning }
    $updateTimer.Start()
})
[System.Windows.Forms.Application]::Run($mainForm)
#endregion
