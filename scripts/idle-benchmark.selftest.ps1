# Tests the rules in IdleBenchmark.Lib.ps1 with made-up numbers. Exit 0 = all passed. Runs in CI before any measurement is trusted.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'IdleBenchmark.Lib.ps1')
$fails = New-Object System.Collections.Generic.List[string]
function Check($name, $ok) { if ($ok) { "ok   $name" } else { "FAIL $name"; $script:fails.Add($name) } }

$plan = [pscustomobject]@{ SampleSeconds = 60; IntervalSeconds = 2 }
function New-Run([hashtable]$over = @{}) {
    $r = [ordered]@{
        appExitedEarly = $false; windowFound = $true; windowMinimized = $false; readErrors = 0; scenarioState = 'ok'; scenarioDetail = ''; pageMismatch = $null
        samples = @(1..30 | ForEach-Object { [pscustomobject]@{ shellCpuPercent = 1.4; webviewCpuPercent = 0.5; totalCpuPercent = 1.9; shellGpuPercent = 0.0; webviewGpuPercent = 0.0 } })
        elapsedSeconds = 60; processesAppearedWhileSampling = 0; crossCheck = [pscustomobject]@{ perSampleSeconds = 1.1; firstToLastSeconds = 1.12 }; systemCpuPercentMean = 20
    }
    foreach ($k in $over.Keys) { $r[$k] = $over[$k] }
    [pscustomobject]$r
}

# --- statistics
$s = Get-Stats @(1.0, 2.0, 3.0, 4.0)
Check 'median of an even count is the mean of the middle two' ($s.median -eq 2.5)
Check 'spread is max minus min' ($s.spread -eq 3)
Check 'stats of nothing is null, not zero' ($null -eq (Get-Stats @()))

# --- validity: a clean run is valid, and every way of being meaningless is inconclusive
Check 'a clean run is valid' (@(Test-RunValidity (New-Run) $plan).Count -eq 0)
Check 'no measurement at all is inconclusive' (@(Test-RunValidity $null $plan).Count -gt 0)
foreach ($case in @(
    @('the app exiting early', @{ appExitedEarly = $true }),
    @('no window', @{ windowFound = $false }),
    @('a minimized window', @{ windowMinimized = $true }),
    @('a process that could not be read', @{ readErrors = 2 }),
    @('a missing scenario report', @{ scenarioState = 'missing' }),
    @('the wrong scenario', @{ scenarioState = 'mismatch'; scenarioDetail = 'panel was closed' }),
    @('the wrong page showing', @{ pageMismatch = 'welcome page instead of the control' }),
    @('too few samples', @{ samples = @(1..10 | ForEach-Object { [pscustomobject]@{ shellCpuPercent = 1; webviewCpuPercent = 1; totalCpuPercent = 2; shellGpuPercent = 0; webviewGpuPercent = 0 } }) }),
    @('sampling that overran', @{ elapsedSeconds = 95 }),
    @('a process tree still settling', @{ processesAppearedWhileSampling = 6 }),
    @('per-sample CPU disagreeing with first-to-last', @{ crossCheck = [pscustomobject]@{ perSampleSeconds = 0.2; firstToLastSeconds = 3.0 } }),
    @('a busy machine', @{ systemCpuPercentMean = 92 }),
    @('machine load that could not be read', @{ systemCpuPercentMean = $null })
)) { Check "$($case[0]) is inconclusive" (@(Test-RunValidity (New-Run $case[1]) $plan).Count -gt 0) }

# --- totals keep the shell and the WebView2 processes apart, and GPU unavailable is null not zero
$t = Get-RunTotals (New-Run)
Check 'shell CPU is reported on its own' ($t.shellCpuMean -eq 1.4)
Check 'WebView2 CPU is reported on its own' ($t.webviewCpuMean -eq 0.5)
$noGpu = New-Run @{ samples = @(1..30 | ForEach-Object { [pscustomobject]@{ shellCpuPercent = 1.4; webviewCpuPercent = 0.5; totalCpuPercent = 1.9; shellGpuPercent = $null; webviewGpuPercent = $null } }) }
$tn = Get-RunTotals $noGpu
Check 'an unavailable GPU counter stays null, never zero' ($null -eq $tn.shellGpuMean -and $null -eq $tn.webviewGpuMean)

# --- summary counts inconclusive runs and leaves them out of the statistics
function Make-Runs($cpus, $verdicts) { for ($i = 0; $i -lt $cpus.Count; $i++) { [pscustomobject]@{ verdict = $verdicts[$i]; problems = if ($verdicts[$i] -eq 'valid') { @() } else { @('x') }; totals = [ordered]@{ shellCpuMean = $cpus[$i]; webviewCpuMean = 0.5; totalCpuMean = $cpus[$i] + 0.5; shellGpuMean = 0.0; webviewGpuMean = 0.0 } } } }
$sum = Get-ScenarioSummary (Make-Runs @(1.4, 1.5, 99, 1.3) @('valid', 'valid', 'inconclusive', 'valid')) $plan
Check 'an inconclusive run is counted but not averaged in' ($sum.runsValid -eq 3 -and $sum.runsInconclusive -eq 1 -and $sum.shellCpuMean.max -eq 1.5)

# --- comparison: separates a real regression from noise, and refuses to decide on too little evidence
$baseline = Get-ScenarioSummary (Make-Runs @(1.35, 1.45, 1.39, 1.42, 1.33) @('valid', 'valid', 'valid', 'valid', 'valid')) $plan
$regressed = Get-ScenarioSummary (Make-Runs @(6.83, 7.21, 7.39, 6.4, 7.0) @('valid', 'valid', 'valid', 'valid', 'valid')) $plan
$same = Get-ScenarioSummary (Make-Runs @(1.5, 1.38, 1.44, 1.41, 1.36) @('valid', 'valid', 'valid', 'valid', 'valid')) $plan
$noisy = Get-ScenarioSummary (Make-Runs @(1.2, 2.6, 1.9, 3.1, 1.4) @('valid', 'valid', 'valid', 'valid', 'valid')) $plan
$few = Get-ScenarioSummary (Make-Runs @(7.0, 7.1) @('valid', 'valid')) $plan
Check 'the known regression is detected against the fixed build' ((Compare-Scenario $regressed $baseline).verdict -eq 'regression')
$regNoisy = Get-ScenarioSummary (Make-Runs @(5.9, 8.7, 6.5, 7.4, 7.1) @('valid', 'valid', 'valid', 'valid', 'valid')) $plan   # the real regression's spread
Check 'a noisy regressed build cannot hide behind its own noise' ((Compare-Scenario $regNoisy $baseline).verdict -eq 'regression')
Check 'the same build is not flagged' ((Compare-Scenario $same $baseline).verdict -eq 'no regression detected')
Check 'a small shift inside normal variation is not flagged' ((Compare-Scenario $noisy $baseline).verdict -ne 'regression')
Check 'two runs are not enough to decide' ((Compare-Scenario $few $baseline).verdict -eq 'inconclusive')
Check 'a missing side is inconclusive' ((Compare-Scenario $null $baseline).verdict -eq 'inconclusive')

if ($fails.Count) { "`n$($fails.Count) FAILED"; exit 1 } else { "`nall passed"; exit 0 }
