#Requires -Version 5.1
<#
    Builds EventLogViewer.ps1 into a standalone, no-console Windows executable using ps2exe.
#>
param(
    [string]$OutputPath = "$PSScriptRoot\dist\EventLogViewer.exe",
    [string]$Version    = '1.0.0.0'
)

if (-not (Get-Module -ListAvailable -Name ps2exe)) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Install-Module -Name ps2exe -Scope CurrentUser -Force
}
Import-Module ps2exe

$distDir = Split-Path $OutputPath -Parent
if (-not (Test-Path $distDir)) { New-Item -ItemType Directory -Path $distDir | Out-Null }

try {
    Invoke-ps2exe -inputFile "$PSScriptRoot\EventLogViewer.ps1" -outputFile $OutputPath `
        -noConsole -STA -title 'Windows Event Log Viewer' -version $Version `
        -company 'Critical MSP' -product 'Event Log Viewer' `
        -copyright "(c) $(Get-Date -Format yyyy) Critical MSP" -requireAdmin:$false `
        -ErrorAction Stop
    Write-Host "Built: $OutputPath" -ForegroundColor Green
} catch {
    Write-Error "ps2exe build failed: $_`n`nIf EventLogViewer.exe is currently running, close it first - the build can't overwrite a locked file."
}
