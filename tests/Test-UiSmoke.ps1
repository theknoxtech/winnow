#Requires -Version 5.1
<#
.SYNOPSIS
    Exercises Winnow's event handlers looking for errors that only appear at runtime.

.DESCRIPTION
    The other two suites test data logic. They cannot see the largest class of bug this app
    actually has: a handler that is wired correctly, parses correctly, and throws the first time
    a user touches it. Two shipped that way -

      - Every preset button raised an error on mouse-leave, because GetNewClosure captures the
        defining scope and $this does not exist there, so $this was $null inside the closure.
      - Search and Search Security Events both threw, because the script runs under
        Set-StrictMode -Version Latest and each caller only set the argument keys it used, so
        Invoke-EventQuery died reading a key that was absent.

    Neither is a parse error and neither touches the preset merge, so nothing caught them.

    This builds the real window - everything up to the launch region, so the form and every
    handler exist but the message loop never starts - then drives the handlers and fails on any
    error of that family. It deliberately does not assert "no errors at all": Get-WinEvent throws
    a caught exception for a log that does not exist on this machine, which is normal and lands
    in $Error regardless.

    Exits non-zero if any assertion fails, so CI can gate on it.

.EXAMPLE
    powershell.exe -STA -File .\tests\Test-UiSmoke.ps1
#>
[CmdletBinding()]
param()

# WinForms needs a single-threaded apartment. Re-launch into one rather than failing obscurely
# half way through, since -File from a default pwsh/powershell host is not guaranteed to be STA.
if ([System.Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    & powershell.exe -STA -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath
    exit $LASTEXITCODE
}

$repoRoot   = Split-Path $PSScriptRoot -Parent
$scriptFile = Join-Path $repoRoot 'Winnow.ps1'
if (-not (Test-Path $scriptFile)) { throw "Cannot find $scriptFile" }

$source = Get-Content $scriptFile -Raw
$cut    = $source.IndexOf('#region 8 - Launch')
if ($cut -lt 0) { throw 'Could not find the launch region marker in Winnow.ps1' }

$failures = 0

# The three ways StrictMode and closure-scope bugs announce themselves. Anything else in $Error
# is a real query failing against this machine, which is not what this suite is about.
$fatalPatterns = @(
    '*cannot be found on this object*'
    '*null-valued expression*'
    '*cannot be retrieved because it has not been set*'
)

function Test-ErrorsClean {
    param([Parameter(Mandatory)] [string] $Stage)

    $bad = @()
    foreach ($record in $Error) {
        $message = $record.Exception.Message
        foreach ($pattern in $fatalPatterns) {
            if ($message -like $pattern) {
                $bad += "line $($record.InvocationInfo.ScriptLineNumber): $message"
                break
            }
        }
    }
    $Error.Clear()

    if ($bad.Count -eq 0) {
        Write-Host "  PASS  $Stage" -ForegroundColor Green
        return
    }
    Write-Host "  FAIL  $Stage" -ForegroundColor Red
    foreach ($entry in ($bad | Select-Object -Unique)) { Write-Host "          $entry" -ForegroundColor Red }
    $script:failures++
}

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

Write-Host 'Winnow UI smoke tests' -ForegroundColor Cyan

Write-Host "`nBuilding the window"
$Error.Clear()
Invoke-Expression $source.Substring(0, $cut)
Test-ErrorsClean 'the window builds without a runtime error'

$presetButtons = @($pnlPresets.Controls | Where-Object { $_ -is [System.Windows.Forms.Button] })
Assert-That 'a button exists per preset' ($presetButtons.Count -eq $script:Presets.Count) "$($presetButtons.Count) buttons, $($script:Presets.Count) presets"

# Suppressed so the suite cannot block on a modal elevation prompt when run unelevated. The
# prompt itself is not what is under test here; the handlers behind it are.
$script:isAdmin = $true

# Show-SearchResults puts a failed query in a MessageBox. In an unattended suite that is a hang,
# not a failure - the first draft of this file deadlocked exactly that way when the bug it was
# written to catch was reintroduced. Capturing the result instead means a broken query fails
# the run in the normal way.
$script:CapturedResults = [System.Collections.Generic.List[object]]::new()
function Show-SearchResults {
    param($Result)
    $null = $script:CapturedResults.Add($Result)
}

function Get-LastQueryError {
    if ($script:CapturedResults.Count -eq 0) { return 'no result was produced' }
    $last = $script:CapturedResults[$script:CapturedResults.Count - 1]
    if ($last -is [hashtable] -and $last.ContainsKey('Error')) { return $last['Error'] }
    return $null
}

Write-Host "`nHover, over every preset button"
# The bug this catches fires on mouse-leave, which no amount of clicking reaches.
# Events are raised through the protected On* methods rather than PerformClick, which is a
# silent no-op on a control whose form was never shown - an earlier draft of this file "passed"
# every click assertion without ever running a handler.
$flags   = [System.Reflection.BindingFlags]::NonPublic -bor [System.Reflection.BindingFlags]::Instance
$onEnter = [System.Windows.Forms.Control].GetMethod('OnMouseEnter', $flags)
$onLeave = [System.Windows.Forms.Control].GetMethod('OnMouseLeave', $flags)
$onClick = [System.Windows.Forms.Control].GetMethod('OnClick', $flags)
Assert-That 'the mouse events can be raised' (($null -ne $onEnter) -and ($null -ne $onLeave) -and ($null -ne $onClick))

$Error.Clear()
$hoverThrew = @()
foreach ($button in $presetButtons) {
    try {
        $onEnter.Invoke($button, @([object][System.EventArgs]::Empty))
        $onLeave.Invoke($button, @([object][System.EventArgs]::Empty))
    } catch {
        $hoverThrew += "$($button.Text): $($_.Exception.InnerException.Message)"
    }
}
Assert-That 'no button throws on hover' ($hoverThrew.Count -eq 0) ($hoverThrew | Select-Object -First 1)
Test-ErrorsClean 'hovering raises no error'

Write-Host "`nClicking every preset button"
$nudMaxEvents.Value = $nudMaxEvents.Minimum
$Error.Clear()
$presetErrors = @()
$ran = 0
foreach ($button in $presetButtons) {
    $before = $script:CapturedResults.Count
    $onClick.Invoke($button, @([object][System.EventArgs]::Empty))
    if ($script:CapturedResults.Count -gt $before) { $ran++ }
    $problem = Get-LastQueryError
    # A log this machine does not have is normal and already handled; a preset that cannot even
    # be turned into a query is not.
    if ($problem -and $problem -notlike '*Log not found*' -and $problem -notlike '*Access denied*') {
        $presetErrors += "$($button.Text): $problem"
    }
}
Test-ErrorsClean 'every preset runs'
# Guards the guard: if the handlers stop being reached, every other assertion here goes quiet
# rather than failing, which is how the no-op PerformClick went unnoticed in the first place.
Assert-That 'every button actually ran a query' ($ran -eq $presetButtons.Count) "$ran of $($presetButtons.Count)"
Assert-That 'no preset fails to build a query' ($presetErrors.Count -eq 0) ($presetErrors | Select-Object -First 1)

Write-Host "`nThe three search entry points"
# Each built a different argument shape, and two of the three were broken. What is under test is
# that the call completes, not what this particular machine happens to hold.
$Error.Clear()
$cboLogSource.Text = 'Application'
Invoke-Search
Test-ErrorsClean 'Search'
Assert-That 'Search builds a valid query' ($null -eq (Get-LastQueryError)) (Get-LastQueryError)

$Error.Clear()
$txtAppName.Text = 'explorer'
Invoke-AppSearch
Test-ErrorsClean 'Find App Events'
Assert-That 'Find App Events builds a valid query' ($null -eq (Get-LastQueryError)) (Get-LastQueryError)

$Error.Clear()
$txtSecUser.Text = 'Administrator'
Invoke-SecurityIdentitySearch
Test-ErrorsClean 'Search Security Events'
$securityProblem = Get-LastQueryError
Assert-That 'Search Security Events builds a valid query' (
    ($null -eq $securityProblem) -or ($securityProblem -like '*Access denied*')
) $securityProblem

Write-Host "`nThe rest of the toolbar"
$Error.Clear()
$txtLiveFilter.Text = 'a'
$txtLiveFilter.Text = ''
Test-ErrorsClean 'the results filter'

$Error.Clear()
Invoke-ClearFilters
Test-ErrorsClean 'Clear'
Assert-That 'Clear resets the status' ($lblStatus.Text -eq 'Ready') $lblStatus.Text

$Error.Clear()
$btnExport.PerformClick()
Test-ErrorsClean 'Export with nothing to export'

Write-Host "`nThe query argument shape"
# The root cause of the search bugs: callers that omitted keys they did not use, against a
# script running under StrictMode where reading an absent key is a terminating error.
$expected = 'Preset', 'FilterHash', 'AppName', 'SecurityIdentity', 'MaxEvents', 'Keyword'
$argument = New-QueryArgument -Preset $null -MaxEvents 1 -Keyword ''
foreach ($key in $expected) {
    Assert-That "the argument always carries $key" ($argument.ContainsKey($key))
}

# The tightest guard on the actual failure, and one that cannot reach a MessageBox at all:
# every argument shape has to survive Invoke-EventQuery reading its way down the branches.
$shapes = @{
    'a preset'       = New-QueryArgument -Preset ($script:Presets[0]) -MaxEvents 100 -Keyword ''
    'a filter hash'  = New-QueryArgument -FilterHash @{ LogName = 'Application' } -MaxEvents 100 -Keyword ''
    'an app name'    = New-QueryArgument -AppName 'explorer' -MaxEvents 100 -Keyword ''
    'an identity'    = New-QueryArgument -SecurityIdentity @{ UserName = 'x'; HostName = ''; IPAddress = '' } -MaxEvents 100 -Keyword ''
}
foreach ($shape in $shapes.GetEnumerator()) {
    $Error.Clear()
    $result = Invoke-EventQuery -Argument $shape.Value
    Assert-That "Invoke-EventQuery accepts $($shape.Key)" ($result -is [hashtable]) "returned $($result.GetType().Name)"
    Test-ErrorsClean "reading the argument for $($shape.Key)"
}

$mainForm.Dispose()

Write-Host ''
if ($failures -gt 0) {
    Write-Host "$failures check(s) failed" -ForegroundColor Red
    exit 1
}
Write-Host 'All UI smoke tests passed' -ForegroundColor Green
