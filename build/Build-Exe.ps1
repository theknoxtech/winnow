#Requires -Version 5.1
<#
.SYNOPSIS
    Builds Winnow.ps1 into a standalone, no-console Windows executable using ps2exe.

.DESCRIPTION
    Produces dist\Winnow.exe. The exe embeds the script and hosts the PowerShell runtime, so it
    needs no separate install and behaves identically to running the script directly - including
    the Backstage host detection, which uses Add-Type at run time and has been verified to work
    inside a ps2exe build.

    Releases ship both this exe and Winnow.ps1. The script is the fallback if the exe is ever
    quarantined: Defender's Wacatac ML classifier applies to PE binaries and cannot apply to a
    .ps1, so having both means an AV false positive never leaves a technician with nothing.

.PARAMETER Version
    Version to stamp into the executable. Should match the release tag; the release workflow
    checks that it does.

.PARAMETER OutputPath
    Where to write the exe. Defaults to dist\Winnow.exe.

.EXAMPLE
    .\build\Build-Exe.ps1
    .\build\Build-Exe.ps1 -Version 2.1.0
#>
[CmdletBinding()]
param(
    [string]$Version = '0.0.0',
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path $PSScriptRoot -Parent
$scriptFile = Join-Path $repoRoot 'Winnow.ps1'
if (-not $OutputPath) { $OutputPath = Join-Path $repoRoot 'dist\Winnow.exe' }

if (-not (Test-Path $scriptFile)) { throw "Source script not found: $scriptFile" }

# ps2exe stamps a four-part file version, so 2.0.0 has to become 2.0.0.0.
$stamp = $Version -replace '^v', ''
$parts = @($stamp -split '\.')
while ($parts.Count -lt 4) { $parts += '0' }
$fileVersion = ($parts[0..3] -join '.')

Write-Host "Building Winnow $stamp" -ForegroundColor Cyan

# Refuse to build a script that does not parse. ps2exe will happily compile a broken script into a
# broken exe, and the failure then only shows up when someone runs it.
$parseErrors = $null
[void][System.Management.Automation.Language.Parser]::ParseFile(
    (Resolve-Path $scriptFile).Path, [ref]$null, [ref]$parseErrors)
if ($parseErrors -and $parseErrors.Count) {
    $parseErrors | ForEach-Object { Write-Host "  line $($_.Extent.StartLineNumber): $($_.Message)" -ForegroundColor Red }
    throw "$scriptFile has $($parseErrors.Count) parse error(s) - not building."
}

if (-not (Get-Module -ListAvailable -Name ps2exe)) {
    Write-Host 'Installing ps2exe...' -ForegroundColor Cyan
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    if (Get-PSRepository -Name PSGallery -ErrorAction SilentlyContinue) {
        Set-PSRepository -Name PSGallery -InstallationPolicy Trusted -ErrorAction SilentlyContinue
    }
    Install-Module -Name ps2exe -Scope CurrentUser -Force -AllowClobber
}
Import-Module ps2exe

$distDir = Split-Path $OutputPath -Parent
if (-not (Test-Path $distDir)) { New-Item -ItemType Directory -Path $distDir -Force | Out-Null }

try {
    # -STA is required for WinForms; -noConsole keeps a console window from flashing up behind
    # the UI, which matters most on a Backstage desktop where it is just confusing.
    Invoke-ps2exe -inputFile $scriptFile -outputFile $OutputPath `
        -noConsole -STA -title 'Winnow' -product 'Winnow' `
        -description 'Windows Event Log triage' `
        -version $fileVersion -requireAdmin:$false `
        -ErrorAction Stop
} catch {
    throw "ps2exe build failed: $_`n`nIf Winnow.exe is currently running, close it first - the build cannot overwrite a locked file."
}

if (-not (Test-Path $OutputPath)) { throw "ps2exe reported success but produced no file at $OutputPath" }

$built   = Get-Item $OutputPath
$stamped = $built.VersionInfo.FileVersion
if ($stamped -and -not $stamped.StartsWith(($parts[0..2] -join '.'))) {
    Write-Warning "Stamped version '$stamped' does not look like '$stamp'."
}

Write-Host ''
Write-Host "Built:   $OutputPath" -ForegroundColor Green
Write-Host "Size:    $([math]::Round($built.Length / 1KB, 1)) KB"
Write-Host "Version: $stamped"
