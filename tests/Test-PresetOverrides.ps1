#Requires -Version 5.1
<#
.SYNOPSIS
    Tests the presets.json override merge in Winnow.ps1.

.DESCRIPTION
    Winnow ships as a script, so there is no compiler and no unit test framework standing between
    an edit and a release. The override merge is the only non-trivial logic in it - partial field
    overlay, disabling, adding, and degrading safely on a malformed file - so it gets real tests.

    Loads Winnow.ps1 up to the UI region, which gives us the functions without building a window,
    then points the preset path at a temporary file and exercises each behaviour.

    Exits non-zero if any assertion fails, so CI can gate on it.

.EXAMPLE
    .\tests\Test-PresetOverrides.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path $PSScriptRoot -Parent
$scriptFile = Join-Path $repoRoot 'Winnow.ps1'
if (-not (Test-Path $scriptFile)) { throw "Cannot find $scriptFile" }

# Everything before the UI region: host detection, presets, and the helper functions. Loading the
# whole file would call Application.Run and block forever.
$source = Get-Content $scriptFile -Raw
$uiIndex = $source.IndexOf('#region 5 - UI Construction')
if ($uiIndex -lt 0) { throw 'Could not find the UI region marker in Winnow.ps1' }
Invoke-Expression $source.Substring(0, $uiIndex)

$testDir = Join-Path ([System.IO.Path]::GetTempPath()) ('winnow-presets-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
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

function Set-Overrides {
    param([Parameter(Mandatory)] [string] $Json)
    Set-Content -Path $script:PresetFilePath -Value $Json -Encoding UTF8
    Import-PresetOverrides
}

function Get-Preset {
    param([Parameter(Mandatory)] [string] $Label)
    return $script:Presets | Where-Object { $_.Label -eq $Label }
}

Write-Host "Winnow preset override tests ($builtInCount built-in presets)" -ForegroundColor Cyan

Write-Host "`nNo override file"
Import-PresetOverrides
Assert-That 'built-ins load unchanged' ($script:Presets.Count -eq $builtInCount) "$($script:Presets.Count)"
Assert-That 'no warning' ([string]::IsNullOrEmpty($script:PresetWarning)) $script:PresetWarning

Write-Host "`nPartial override of an existing preset"
Set-Overrides '{ "presets": [ { "label": "Logon Events", "id": [4624, 4625, 9999] } ] }'
$logon = Get-Preset 'Logon Events'
Assert-That 'count unchanged' ($script:Presets.Count -eq $builtInCount) "$($script:Presets.Count)"
Assert-That 'event ids replaced' (($logon.Id -join ',') -eq '4624,4625,9999') ($logon.Id -join ',')
Assert-That 'ids coerced to Int32' ($logon.Id[0] -is [int]) $logon.Id[0].GetType().Name
Assert-That 'unnamed fields kept from built-in' ($logon.LogName -eq 'Security' -and [bool]$logon.Description)

Write-Host "`nDisabling a built-in"
Set-Overrides '{ "presets": [ { "label": "Print Jobs", "disabled": true } ] }'
Assert-That 'removed from the set' (-not (Get-Preset 'Print Jobs'))
Assert-That 'count reduced by one' ($script:Presets.Count -eq ($builtInCount - 1)) "$($script:Presets.Count)"

Write-Host "`nAdding a new preset"
Set-Overrides '{ "presets": [ { "group": "Custom", "label": "LOB App", "logName": "Application", "id": [4001], "providerName": ["Acme"] } ] }'
$added = Get-Preset 'LOB App'
Assert-That 'added to the set' ([bool]$added)
Assert-That 'count increased by one' ($script:Presets.Count -eq ($builtInCount + 1)) "$($script:Presets.Count)"
Assert-That 'providerName is an array' ($added.ProviderName -is [array])
Assert-That 'group defaults are applied' ($added.Group -eq 'Custom')

Write-Host "`nField names are case-insensitive"
Set-Overrides '{ "presets": [ { "LABEL": "Disk Errors", "LOGNAME": "System", "ID": [7] } ] }'
Assert-That 'matched regardless of casing' (((Get-Preset 'Disk Errors').Id -join ',') -eq '7')

Write-Host "`nMalformed input degrades safely"
Set-Overrides '{ this is not json'
Assert-That 'falls back to built-ins' ($script:Presets.Count -eq $builtInCount) "$($script:Presets.Count)"
Assert-That 'warning is surfaced' ([bool]$script:PresetWarning)

Set-Overrides '{ "somethingElse": 1 }'
Assert-That 'missing presets array falls back' ($script:Presets.Count -eq $builtInCount)
Assert-That 'warning is surfaced' ([bool]$script:PresetWarning)

Set-Overrides '{ "presets": [ { "label": "No Log Name", "id": [1] } ] }'
Assert-That 'new preset without logName is skipped' (-not (Get-Preset 'No Log Name'))
Assert-That 'warning is surfaced' ([bool]$script:PresetWarning)

Write-Host "`nReloading does not stack overrides"
Set-Overrides '{ "presets": [ { "label": "Print Jobs", "disabled": true } ] }'
$afterFirst = $script:Presets.Count
Import-PresetOverrides
Import-PresetOverrides
Assert-That 'count stable across repeated reloads' ($script:Presets.Count -eq $afterFirst) "$afterFirst then $($script:Presets.Count)"

Write-Host "`nGenerated template"
[System.IO.File]::Delete($script:PresetFilePath)
New-PresetTemplateFile
Import-PresetOverrides
Assert-That 'template is valid JSON' ([string]::IsNullOrEmpty($script:PresetWarning)) $script:PresetWarning
Assert-That 'template changes nothing until edited' ($script:Presets.Count -eq $builtInCount) "$($script:Presets.Count)"

[System.IO.Directory]::Delete($testDir, $true)

Write-Host ''
if ($failures -gt 0) {
    Write-Host "$failures assertion(s) failed" -ForegroundColor Red
    exit 1
}
Write-Host 'All preset override tests passed' -ForegroundColor Green
