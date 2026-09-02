#Requires -Version 5.1
<#
.SYNOPSIS
    Builds Winnow.exe as a single self-contained file.

.DESCRIPTION
    Produces one executable with the Core library and Newtonsoft.Json embedded (via Costura), so
    a single file can be copied into a ScreenConnect Backstage session with nothing beside it.

    Targets .NET Framework 4.8, which ships in-box on Windows 10 1903+, Windows 11 and
    Server 2019+ - there is nothing to install on the target machine.

.PARAMETER Version
    Version to stamp into the assembly. The in-app update check reads this back out of the
    assembly and compares it against the latest GitHub release, so it must match the release tag.
    Defaults to 0.0.0-dev for local builds.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.PARAMETER OutputPath
    Where to place the finished exe. Defaults to dist\Winnow.exe.

.PARAMETER SkipTests
    Skip the test suite. Not recommended; intended for iterating on packaging only.

.EXAMPLE
    .\build\publish.ps1
    .\build\publish.ps1 -Version 1.3.0
#>
[CmdletBinding()]
param(
    [string]$Version = '0.0.0-dev',
    [string]$Configuration = 'Release',
    [string]$OutputPath,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
$wpfProject = Join-Path $repoRoot 'src\Winnow.App\Winnow.App.csproj'
$testProject = Join-Path $repoRoot 'tests\Winnow.Tests\Winnow.Tests.csproj'

if (-not $OutputPath) { $OutputPath = Join-Path $repoRoot 'dist\Winnow.exe' }

# A tag like v1.3.0 is accepted as well as a bare 1.3.0.
$assemblyVersion = $Version -replace '^v', ''

Write-Host "Building Winnow $assemblyVersion ($Configuration)" -ForegroundColor Cyan

if (-not $SkipTests) {
    Write-Host 'Running tests...' -ForegroundColor Cyan
    dotnet test $testProject -c $Configuration --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed - not publishing.' }
}

Write-Host 'Building executable...' -ForegroundColor Cyan
dotnet build $wpfProject -c $Configuration --nologo -v quiet `
    "/p:Version=$assemblyVersion" `
    "/p:InformationalVersion=$assemblyVersion"
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$built = Join-Path $repoRoot "src\Winnow.App\bin\$Configuration\net48\Winnow.exe"
if (-not (Test-Path $built)) { throw "Expected build output not found: $built" }

$distDir = Split-Path $OutputPath -Parent
if (-not (Test-Path $distDir)) { New-Item -ItemType Directory -Path $distDir -Force | Out-Null }
Copy-Item $built $OutputPath -Force

# The whole point of the packaging step is that this is one file. If Costura silently stopped
# embedding, the exe would still build and still run here - and then fail on a machine where the
# loose DLLs were not copied alongside it. Verifying the count is what makes that non-silent.
$loose = Get-ChildItem (Split-Path $built -Parent) -Filter *.dll -ErrorAction SilentlyContinue
if ($loose) {
    Write-Warning ("Expected no loose DLLs beside the exe, found: " + ($loose.Name -join ', '))
    Write-Warning 'Costura may not be embedding dependencies - the exe is probably not standalone.'
}

$size = [math]::Round((Get-Item $OutputPath).Length / 1MB, 2)
$stamped = (Get-Item $OutputPath).VersionInfo.FileVersion

Write-Host ''
Write-Host "Built:   $OutputPath" -ForegroundColor Green
Write-Host "Size:    $size MB"
Write-Host "Version: $stamped"
Write-Host ''
Write-Host 'This is a single self-contained file - copy just the exe to the target machine.'
