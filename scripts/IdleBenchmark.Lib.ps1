# Pure functions for scripts/idle-benchmark.ps1: statistics, the rules that make a run inconclusive, and the comparison of two result sets.
# No process is started and nothing is measured here, so every rule can be tested with made-up numbers (idle-benchmark.selftest.ps1).

$script:GpuNote = 'GPU is reported separately from CPU on purpose. Low CPU does not mean no GPU activity, and neither means zero idle cost: memory, wake-ups and battery are separate costs this does not measure.'

function Get-Stats([double[]]$xs) {
    $xs = @($xs | Where-Object { $null -ne $_ })
    if ($xs.Count -eq 0) { return $null }
    $sorted = $xs | Sort-Object
    $n = $sorted.Count
    $median = if ($n % 2 -eq 1) { $sorted[($n - 1) / 2] } else { ($sorted[$n / 2 - 1] + $sorted[$n / 2]) / 2 }
    $mean = ($xs | Measure-Object -Average).Average
    $var = if ($n -gt 1) { (($xs | ForEach-Object { ($_ - $mean) * ($_ - $mean) }) | Measure-Object -Sum).Sum / ($n - 1) } else { 0 }
    [ordered]@{
        n = $n; min = [math]::Round($sorted[0], 3); median = [math]::Round($median, 3); max = [math]::Round($sorted[$n - 1], 3)
        mean = [math]::Round($mean, 3); stdev = [math]::Round([math]::Sqrt($var), 3); spread = [math]::Round($sorted[$n - 1] - $sorted[0], 3)
    }
}

# A run is trusted only if every check that could have made it meaningless came out clean. Anything else is INCONCLUSIVE, never a number to average in.
# $run is the raw record the harness writes; $plan is what was asked for.
function Test-RunValidity($run, $plan) {
    $problems = New-Object System.Collections.Generic.List[string]
    if ($null -eq $run) { return @('no measurement was produced') }
    if ($run.appExitedEarly) { $problems.Add('the app exited during the run') }
    if (-not $run.windowFound) { $problems.Add('no app window was found') }
    if ($run.windowMinimized) { $problems.Add('the window was minimized, which changes what it costs') }
    if ($run.readErrors -gt 0) { $problems.Add("$($run.readErrors) live process(es) could not be read") }
    if ($run.scenarioState -eq 'missing') { $problems.Add('the app never reported the scenario it was put in') }
    elseif ($run.scenarioState -eq 'mismatch') { $problems.Add("the app was not in the requested scenario ($($run.scenarioDetail))") }
    if ($run.pageMismatch) { $problems.Add("the page showing was not the requested one ($($run.pageMismatch))") }
    $expected = [math]::Max(1, [int]($plan.SampleSeconds / $plan.IntervalSeconds))
    if ($run.samples.Count -lt [math]::Ceiling(0.9 * $expected)) { $problems.Add("only $($run.samples.Count) of $expected samples were taken") }
    if ($run.elapsedSeconds -gt 0 -and [math]::Abs($run.elapsedSeconds - $plan.SampleSeconds) -gt 0.25 * $plan.SampleSeconds) { $problems.Add("sampling took $([math]::Round($run.elapsedSeconds, 1)) s instead of $($plan.SampleSeconds) s") }
    if ($run.processesAppearedWhileSampling -gt 3) { $problems.Add("$($run.processesAppearedWhileSampling) processes appeared while sampling: the tree was still settling") }
    $cc = $run.crossCheck
    if ($null -ne $cc -and [math]::Abs($cc.perSampleSeconds - $cc.firstToLastSeconds) -gt [math]::Max(0.25, 0.25 * $cc.firstToLastSeconds)) {
        $problems.Add("per-sample CPU ($([math]::Round($cc.perSampleSeconds, 2)) s) disagrees with first-to-last CPU ($([math]::Round($cc.firstToLastSeconds, 2)) s)")
    }
    if ($null -ne $run.systemCpuPercentMean -and $run.systemCpuPercentMean -gt 80) { $problems.Add("the whole machine was busy ($([math]::Round($run.systemCpuPercentMean))% CPU), so the reading says little about the app") }
    return @($problems.ToArray())
}

function Get-RunTotals($run) {
    # Mean over the sampling window, as percent of ONE core, for each group separately: the shell (JevBrowse.App), the WebView2 processes, everything.
    $shell = @($run.samples | ForEach-Object { $_.shellCpuPercent }); $web = @($run.samples | ForEach-Object { $_.webviewCpuPercent }); $tot = @($run.samples | ForEach-Object { $_.totalCpuPercent })
    # GPU counters are sampled by a separate collector, so they live in gpuSamples when present (and in samples for hand-made records).
    $gsrc = if ($run.PSObject.Properties.Name -contains 'gpuSamples') { @($run.gpuSamples) } else { @($run.samples) }
    $gs = @($gsrc | Where-Object { $null -ne $_.shellGpuPercent } | ForEach-Object { $_.shellGpuPercent }); $gw = @($gsrc | Where-Object { $null -ne $_.webviewGpuPercent } | ForEach-Object { $_.webviewGpuPercent })
    [ordered]@{
        shellCpuMean = if ($shell.Count) { [math]::Round(($shell | Measure-Object -Average).Average, 3) } else { $null }
        shellCpuPeak = if ($shell.Count) { [math]::Round(($shell | Measure-Object -Maximum).Maximum, 3) } else { $null }
        webviewCpuMean = if ($web.Count) { [math]::Round(($web | Measure-Object -Average).Average, 3) } else { $null }
        webviewCpuPeak = if ($web.Count) { [math]::Round(($web | Measure-Object -Maximum).Maximum, 3) } else { $null }
        totalCpuMean = if ($tot.Count) { [math]::Round(($tot | Measure-Object -Average).Average, 3) } else { $null }
        # $null means "the counter was unavailable", never "zero": a machine without GPU counters must not look like a machine with an idle GPU.
        shellGpuMean = if ($gs.Count) { [math]::Round(($gs | Measure-Object -Average).Average, 3) } else { $null }
        webviewGpuMean = if ($gw.Count) { [math]::Round(($gw | Measure-Object -Average).Average, 3) } else { $null }
    }
}

function Get-ScenarioSummary($runs, $plan) {
    $valid = @($runs | Where-Object { $_.verdict -eq 'valid' })
    $bad = @($runs | Where-Object { $_.verdict -ne 'valid' })
    $sum = [ordered]@{ runsAttempted = @($runs).Count; runsValid = $valid.Count; runsInconclusive = $bad.Count; inconclusiveReasons = @($bad | ForEach-Object { $_.problems } | Select-Object -Unique) }
    foreach ($m in 'shellCpuMean', 'webviewCpuMean', 'totalCpuMean', 'shellGpuMean', 'webviewGpuMean') {
        $vals = @($valid | ForEach-Object { $_.totals[$m] } | Where-Object { $null -ne $_ })
        $sum[$m] = Get-Stats $vals
    }
    $sum
}

# Compare a candidate scenario with a baseline scenario on the SHELL's CPU (the part this project's own code controls). Refuses to decide without
# enough valid runs on both sides. The threshold is the larger of a fixed floor and a multiple of the observed run-to-run spread, so it is derived
# from measured variation, not chosen by feel; it is only enforced when the caller says so.
function Compare-Scenario($candidate, $baseline, [double]$MinPoints = 1.0, [double]$SpreadFactor = 3.0, [int]$MinRuns = 3) {
    if ($null -eq $candidate -or $null -eq $baseline) { return [ordered]@{ verdict = 'inconclusive'; reason = 'a result set is missing' } }
    $c = $candidate.shellCpuMean; $b = $baseline.shellCpuMean
    if ($null -eq $c -or $null -eq $b -or $c.n -lt $MinRuns -or $b.n -lt $MinRuns) {
        return [ordered]@{ verdict = 'inconclusive'; reason = "needs at least $MinRuns valid runs on both sides (candidate $($c.n), baseline $($b.n))" }
    }
    $delta = $c.median - $b.median
    $threshold = [math]::Max($MinPoints, $SpreadFactor * [math]::Max($b.spread, $c.spread))
    $separated = $c.min -gt $b.max            # every candidate run is above every baseline run: not noise
    [ordered]@{
        verdict = if ($delta -gt $threshold -and $separated) { 'regression' } elseif ($delta -gt $threshold) { 'suspect' } else { 'no regression detected' }
        candidateMedianShellCpu = $c.median; baselineMedianShellCpu = $b.median; deltaPoints = [math]::Round($delta, 3)
        thresholdPoints = [math]::Round($threshold, 3); runsSeparated = $separated
        reason = "median difference $([math]::Round($delta, 2)) points against a threshold of $([math]::Round($threshold, 2)) (larger of $MinPoints and $SpreadFactor x the widest run-to-run spread)"
    }
}
