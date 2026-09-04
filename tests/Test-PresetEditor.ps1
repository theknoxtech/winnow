#Requires -Version 5.1
<#
.SYNOPSIS
    Tests the preset editor's save path in Winnow.ps1.

.DESCRIPTION
    The editor writes presets.json as a delta against the built-ins, and Import-PresetOverrides
    reads it back. Those two have to agree exactly: a writer that emits too little silently
    reverts someone's edit on the next launch, and one that emits too much pins a preset to
    today's built-in definition forever.

    So the property under test throughout is round-tripping - export the edited set, import it
    again, and assert the result matches what was exported. The delta itself is checked too,
    because "round-trips correctly" is also true of a writer that just dumps everything.

    Loads Winnow.ps1 up to the UI region, which gives the functions without building a window.
    Exits non-zero if any assertion fails, so CI can gate on it.

.EXAMPLE
    .\tests\Test-PresetEditor.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path $PSScriptRoot -Parent
$scriptFile = Join-Path $repoRoot 'Winnow.ps1'
if (-not (Test-Path $scriptFile)) { throw "Cannot find $scriptFile" }

$source  = Get-Content $scriptFile -Raw
$uiIndex = $source.IndexOf('#region 5 - UI Construction')
if ($uiIndex -lt 0) { throw 'Could not find the UI region marker in Winnow.ps1' }
Invoke-Expression $source.Substring(0, $uiIndex)

$testDir = Join-Path ([System.IO.Path]::GetTempPath()) ('winnow-editor-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $testDir | Out-Null
$script:PresetFilePath = Join-Path $testDir 'presets.json'

$builtInCount = $script:BuiltInPresets.Count
$failures     = 0

function Assert-That {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [bool]   $Condition,
        [string] $Detail
    )
    if ($Condition) {
        Write-Host "  PASS  $Name" -ForegroundColor Green
    } else {
        Write-Host "  FAIL  $Name$(if ($Detail) { " - $Detail" })" -ForegroundColor Red
        $script:failures++
    }
}

function Get-EditedSet {
    # A fresh working copy of the built-ins, the way the editor takes one.
    $set = [System.Collections.Generic.List[object]]::new()
    foreach ($preset in $script:BuiltInPresets) { $null = $set.Add((Copy-Preset $preset)) }
    return ,$set
}

function Get-Preset {
    param([Parameter(Mandatory)] [string] $Label, $From)
    if (-not $From) { $From = $script:Presets }
    return $From | Where-Object { $_['Label'] -eq $Label }
}

function Save-AndReload {
    param([Parameter(Mandatory)] $Presets)
    $written = Export-PresetOverrides -Presets $Presets
    Import-PresetOverrides
    return $written
}

function Get-SavedJson {
    return (Get-Content $script:PresetFilePath -Raw | ConvertFrom-Json)
}

Write-Host "Winnow preset editor tests ($builtInCount built-in presets)" -ForegroundColor Cyan

Write-Host "`nAn untouched set writes no overrides"
$written = Save-AndReload (Get-EditedSet)
Assert-That 'nothing is written' ($written -eq 0) "$written entries"
Assert-That 'all built-ins survive the round trip' ($script:Presets.Count -eq $builtInCount) "$($script:Presets.Count)"
Assert-That 'no warning' ([string]::IsNullOrEmpty($script:PresetWarning)) $script:PresetWarning

Write-Host "`nOne changed field writes one small entry"
$set = Get-EditedSet
(Get-Preset 'Logon Events' $set)['Id'] = @(4624, 4625)
$written = Save-AndReload $set
Assert-That 'exactly one entry written' ($written -eq 1) "$written"

$entry = @((Get-SavedJson).presets)[0]
Assert-That 'entry is keyed by label' ($entry.label -eq 'Logon Events') "$($entry.label)"
Assert-That 'only label and id are stored' (@($entry.PSObject.Properties).Count -eq 2) (@($entry.PSObject.Properties.Name) -join ',')
Assert-That 'the edit round-trips' (((Get-Preset 'Logon Events').Id -join ',') -eq '4624,4625') ((Get-Preset 'Logon Events').Id -join ',')
Assert-That 'untouched fields still come from the built-in' ((Get-Preset 'Logon Events').LogName -eq 'Security')
Assert-That 'every other preset is untouched' ($script:Presets.Count -eq $builtInCount) "$($script:Presets.Count)"

Write-Host "`nA hidden built-in is recorded, not just omitted"
$set = Get-EditedSet
$null = $set.Remove((Get-Preset 'Print Jobs' $set))
$written = Save-AndReload $set
Assert-That 'one entry written' ($written -eq 1) "$written"
Assert-That 'written as disabled' (@((Get-SavedJson).presets)[0].disabled -eq $true)
Assert-That 'stays hidden after reload' (-not (Get-Preset 'Print Jobs'))
Assert-That 'count reduced by one' ($script:Presets.Count -eq ($builtInCount - 1)) "$($script:Presets.Count)"

Write-Host "`nUn-hiding it removes the entry again"
$written = Save-AndReload (Get-EditedSet)
Assert-That 'nothing is written' ($written -eq 0) "$written"
Assert-That 'the built-in is back' ([bool](Get-Preset 'Print Jobs'))

Write-Host "`nA custom preset is written in full"
$set = Get-EditedSet
$null = $set.Add([ordered]@{
    Group = 'Custom'; Label = 'LOB App'; LogName = 'Application'
    Id = @(4001, 4002); ProviderName = @('Acme'); Description = 'Line-of-business errors'
})
$written = Save-AndReload $set
Assert-That 'one entry written' ($written -eq 1) "$written"

$added = Get-Preset 'LOB App'
Assert-That 'present after reload' ([bool]$added)
Assert-That 'event ids round-trip' (($added.Id -join ',') -eq '4001,4002') ($added.Id -join ',')
Assert-That 'providers round-trip' (($added.ProviderName -join ',') -eq 'Acme') ($added.ProviderName -join ',')
Assert-That 'description round-trips' ($added.Description -eq 'Line-of-business errors') $added.Description
Assert-That 'count increased by one' ($script:Presets.Count -eq ($builtInCount + 1)) "$($script:Presets.Count)"

Write-Host "`nClearing a field beats the built-in rather than being ignored"
# Disk Errors is scoped to the disk providers. Removing that scoping has to survive a reload,
# which it only does if an emptied field is written out explicitly rather than being treated
# as "unchanged" and dropped from the delta.
$set = Get-EditedSet
(Get-Preset 'Disk Errors' $set)['ProviderName'] = @()
$written = Save-AndReload $set
Assert-That 'one entry written' ($written -eq 1) "$written"
Assert-That 'the field stays empty after reload' (@((Get-Preset 'Disk Errors').ProviderName).Count -eq 0) ((Get-Preset 'Disk Errors').ProviderName -join ',')

Write-Host "`nA scalar ProviderName is not mistaken for a change"
# Some built-ins declare ProviderName as a bare string and others as an array. The comparison
# has to see through that, or those presets would be written out on every single save.
$scalar = $script:BuiltInPresets | Where-Object { $_['ProviderName'] -is [string] } | Select-Object -First 1
Assert-That 'a scalar-provider built-in exists to test' ([bool]$scalar)
if ($scalar) {
    Assert-That 'array and scalar forms compare equal' (Test-PresetValueEqual $scalar['ProviderName'] @($scalar['ProviderName']))
    Assert-That 'it produces no override entry' ($null -eq (ConvertTo-PresetOverrideEntry -Preset (Copy-Preset $scalar) -BuiltIn $scalar))
}

Write-Host "`nEvery change type at once still round-trips"
$set = Get-EditedSet
(Get-Preset 'Logon Events' $set)['Id']         = @(4624)
(Get-Preset 'Disk Errors'  $set)['Description'] = 'Reworded'
$null = $set.Remove((Get-Preset 'Print Jobs' $set))
$null = $set.Remove((Get-Preset 'App Hangs'  $set))
$null = $set.Add([ordered]@{
    Group = 'Custom'; Label = 'Two Logs'; LogName = 'System'; Id = @(1)
    LogName2 = 'Application'; Id2 = @(2); Description = ''
})
$written = Save-AndReload $set

Assert-That 'five entries written' ($written -eq 5) "$written"
Assert-That 'count is built-ins minus two plus one' ($script:Presets.Count -eq ($builtInCount - 1)) "$($script:Presets.Count)"
Assert-That 'the id edit survived' (((Get-Preset 'Logon Events').Id -join ',') -eq '4624')
Assert-That 'the description edit survived' ((Get-Preset 'Disk Errors').Description -eq 'Reworded')
Assert-That 'both hides survived' ((-not (Get-Preset 'Print Jobs')) -and (-not (Get-Preset 'App Hangs')))
Assert-That 'the second log survived' ((Get-Preset 'Two Logs').LogName2 -eq 'Application')
Assert-That 'its second-log ids survived' (((Get-Preset 'Two Logs').Id2 -join ',') -eq '2')

Write-Host "`nSaving twice in a row changes nothing"
# Guards against a writer whose output does not re-import to the same thing. Any drift here
# would compound on every save the user makes.
$firstJson = Get-Content $script:PresetFilePath -Raw
$written   = Save-AndReload $script:Presets
Assert-That 'the file is byte-identical' ((Get-Content $script:PresetFilePath -Raw) -eq $firstJson)
Assert-That 'nothing was added or dropped' ($written -eq 5) "$written"
Assert-That 'the preset count is stable' ($script:Presets.Count -eq ($builtInCount - 1)) "$($script:Presets.Count)"

Write-Host "`nField parsing"
Assert-That 'ids split on commas'     (((Read-IdList '4624, 4625') -join ',') -eq '4624,4625')
Assert-That 'ids split on whitespace' (((Read-IdList "1 2`t3") -join ',') -eq '1,2,3')
Assert-That 'empty text gives no ids' ((Read-IdList '   ').Count -eq 0)
Assert-That 'ids are Int32'           ((Read-IdList '7')[0] -is [int])

$rejected = $false
try { $null = Read-IdList '4624, notanumber' } catch { $rejected = $true }
Assert-That 'a non-numeric id is rejected, not dropped' $rejected

Assert-That 'names split and trim'     (((Read-NameList ' disk , Microsoft-Windows-Disk ') -join '|') -eq 'disk|Microsoft-Windows-Disk')
Assert-That 'empty names are dropped'  ((Read-NameList 'a,,b').Count -eq 2)
Assert-That 'a list formats back'      ((Format-PresetList @(1,2,3)) -eq '1, 2, 3')
Assert-That 'a scalar formats back'    ((Format-PresetList 'x') -eq 'x')
Assert-That 'empty formats to nothing' ((Format-PresetList @()) -eq '')

Write-Host "`nEditor rows"
$rows = New-EditorRowList
Assert-That 'a row exists per preset, plus the hidden ones' ($rows.Count -eq ($script:Presets.Count + 2)) "$($rows.Count)"
# Not a detail: if the pipeline enumerates the list into a fixed-size array on the way out,
# New, Clone and Delete all throw the moment they are used.
Assert-That 'the list can still be added to' ($rows -is [System.Collections.Generic.List[object]]) $rows.GetType().Name
Assert-That 'hidden built-ins are listed' ([bool]($rows | Where-Object { $_.Hidden -and $_.Preset['Label'] -eq 'Print Jobs' }))
Assert-That 'a hidden row reports Hidden'      ((Get-EditorRowStatus ($rows | Where-Object { $_.Preset['Label'] -eq 'Print Jobs' }))       -eq 'Hidden')
Assert-That 'an edited row reports Modified'   ((Get-EditorRowStatus ($rows | Where-Object { $_.Preset['Label'] -eq 'Logon Events' }))     -eq 'Modified')
Assert-That 'an untouched row reports Built-in' ((Get-EditorRowStatus ($rows | Where-Object { $_.Preset['Label'] -eq 'Account Lockouts' })) -eq 'Built-in')
Assert-That 'a custom row reports Custom'      ((Get-EditorRowStatus ($rows | Where-Object { $_.Preset['Label'] -eq 'Two Logs' }))         -eq 'Custom')

Write-Host "`nThe editor works on a copy"
# Cancel has to be a real cancel, which it only is if the rows are deep copies - a shallow one
# would share the Id array with the live preset and edit it in place.
$live = Get-Preset 'Account Lockouts'
$row  = $rows | Where-Object { $_.Preset['Label'] -eq 'Account Lockouts' }
$row.Preset['Id'] = @(9999)
Assert-That 'editing a row leaves the live preset alone' (($live.Id -join ',') -eq '4740') ($live.Id -join ',')

$sharedRow = $rows | Where-Object { $_.Preset['Label'] -eq 'Logon Events' }
$sharedRow.Preset['Id'][0] = 1
Assert-That 'array contents are copied too' ((Get-Preset 'Logon Events').Id[0] -eq 4624) "$((Get-Preset 'Logon Events').Id[0])"

Assert-That 'a unique name is suggested for a duplicate' ((New-UniqueEditorLabel -Base 'Two Logs' -Rows $rows) -ne 'Two Logs')
Assert-That 'an unused name is left alone' ((New-UniqueEditorLabel -Base 'Nothing Called This' -Rows $rows) -eq 'Nothing Called This')

[System.IO.Directory]::Delete($testDir, $true)

Write-Host ''
if ($failures -gt 0) {
    Write-Host "$failures assertion(s) failed" -ForegroundColor Red
    exit 1
}
Write-Host 'All preset editor tests passed' -ForegroundColor Green
