<#
.SYNOPSIS
  Repeatable measurement of what the app costs at rest: CPU (and, separately, GPU) for the browser shell and for its WebView2 processes.

.DESCRIPTION
  For each scenario and run: start the app fresh, put it in a known state, WAIT (warm-up), then sample for a fixed period and record every
  sample, per process. CPU is percent of ONE core. GPU is the sum of the Windows "GPU Engine" utilisation counters for the group's processes.
  The shell (JevBrowse.App.exe) and the WebView2 processes are reported separately for both.

  Scenarios:  static        the local welcome page, nothing else
              panel-open    the Receipt side panel showing
              panel-closed  the panel opened and then closed (must leave nothing behind)
              agent-idle    an agent session open with one hidden static page, no requests
              busy-control  a deliberately busy page (canvas every frame, CSS animation, hot timer): the POSITIVE CONTROL. If this does not read as busy
                            the instrument is not measuring activity, and the whole result is inconclusive.

  A run is INCONCLUSIVE, never a number to average in, if anything could have made it meaningless: the app exiting, no window, a minimised window,
  a process that could not be read, the app not in the scenario it was asked for, the wrong page, too few or overlong samples, a process tree still
  settling, per-sample CPU disagreeing with first-to-last CPU, or a busy machine (see IdleBenchmark.Lib.ps1, tested by idle-benchmark.selftest.ps1).

  Everything is preserved: <out>\raw\<scenario>-run<N>.json (every sample, every process), environment.json, results.json and summary.md.

  It only ever stops the process tree it started, and creates a new uniquely named folder under -Root; it never deletes anything.

  Limits, stated plainly: one machine per result set; a debug build unless -Configuration release or -ExePath; an app in front of a desktop it does not
  control; the GPU counter can be unavailable (reported as unavailable, never as zero). "Low CPU" is not "no GPU activity" and neither is "zero idle
  cost": memory, wake-ups and battery are separate costs this does not measure.

.PARAMETER ReportOnly   Publish the numbers and never fail the build over them (shared CI runners). Without it, an inconclusive suite exits 2.
.PARAMETER CompareTo    A results.json from another build. Prints a comparison per scenario; only -Enforce turns a regression into exit 1.
#>
param(
    [string[]]$Scenarios = @('static'),   # with -File, pass one comma-separated string: static,panel-open
    [int]$Runs = 5,
    [int]$WarmupSeconds = 30,
    [int]$SampleSeconds = 60,
    [int]$IntervalSeconds = 2,
    [string]$Configuration = 'debug',
    [string]$ExePath = '',
    [string]$Root = 'D:\Browser\_ui-check\idle-benchmark',
    [string]$Label = '',
    [switch]$ReportOnly,
    [string]$CompareTo = '',
    [switch]$Enforce
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'IdleBenchmark.Lib.ps1')
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class IdleWin { [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h); [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow(); }
'@

$Scenarios = @($Scenarios | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$bad = @($Scenarios | Where-Object { $_ -notin 'static','panel-open','panel-closed','agent-idle','busy-control' }); if ($bad) { throw "Unknown scenario: $($bad -join ', ')" }
$rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\')
if ($Root -notmatch '^[A-Za-z]:[\\/]' -or $rootFull.Length -lt 8) { throw "Refusing test root '$Root'." }
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
if ($rootFull -eq $repoRoot -or $rootFull.StartsWith($repoRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing test root '$rootFull': output must not be mixed into the repository." }
$exe = if ($ExePath) { $ExePath } else { Join-Path $PSScriptRoot "..\artifacts\bin\JevBrowse.App\${Configuration}_win-x64\JevBrowse.App.exe" }
if (-not (Test-Path $exe)) { throw "No app at '$exe'. Build it first (or pass -ExePath)." }
$exe = (Resolve-Path $exe).Path
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$out = Join-Path $rootFull ("{0}{1}-{2}" -f $(if ($Label) { "$Label-" } else { '' }), $stamp, [guid]::NewGuid().ToString('N').Substring(0, 6))
New-Item -ItemType Directory -Path "$out\raw" -Force | Out-Null
$plan = [pscustomobject]@{ Scenarios = $Scenarios; Runs = $Runs; WarmupSeconds = $WarmupSeconds; SampleSeconds = $SampleSeconds; IntervalSeconds = $IntervalSeconds }

# The positive control page: worst-case behaviour for a page that is only sitting there.
$busyHtml = '<!doctype html><html><head><meta charset=utf-8><title>busy control</title><style>@keyframes spin{to{transform:rotate(360deg)}}#a{width:80px;height:80px;background:#08c;animation:spin 1s linear infinite}</style></head><body><div id=a></div><canvas id=c width=600 height=400></canvas><script>const g=document.getElementById("c").getContext("2d");let t=0;function f(){t++;g.clearRect(0,0,600,400);for(let i=0;i<300;i++){g.beginPath();g.arc(300+Math.cos(t/20+i)*200,200+Math.sin(t/17+i)*150,8,0,6.3);g.fill();}requestAnimationFrame(f);}f();let x=0;setInterval(()=>{for(let i=0;i<20000;i++)x+=Math.sqrt(i);},4);</script></body></html>'
$busyUrl = 'data:text/html,' + [Uri]::EscapeDataString($busyHtml)

# ---------------- environment ----------------
function Get-Environment {
    $os = Get-CimInstance Win32_OperatingSystem; $cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
    $gpus = @(Get-CimInstance Win32_VideoController | ForEach-Object { [ordered]@{ name = $_.Name; driver = $_.DriverVersion } })
    $power = try { ((powercfg /getactivescheme) -join ' ') -replace '^.*\((.*)\).*$', '$1' } catch { 'unknown' }
    $sha = try { (git -C $repoRoot rev-parse HEAD 2>$null) } catch { $null }
    $dirty = try { @(git -C $repoRoot status --porcelain 2>$null).Count } catch { $null }
    $wv = try { (Get-Process msedgewebview2 -ErrorAction Stop | Select-Object -First 1).MainModule.FileVersionInfo.ProductVersion } catch { $null }
    if (-not $wv) { $wv = try { (Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}' -ErrorAction Stop).pv } catch { 'not found' } }
    $hostHash = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($env:COMPUTERNAME))).Replace('-', '').Substring(0, 8).ToLower()
    [ordered]@{
        capturedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
        machineIdHash = $hostHash   # a hash, not the name
        os = "$($os.Caption) $($os.Version)"; cpuModel = $cpu.Name.Trim(); logicalCores = [Environment]::ProcessorCount
        ramGB = [math]::Round($os.TotalVisibleMemorySize / 1MB, 1); gpus = $gpus; powerPlan = $power
        dotnet = [Runtime.InteropServices.RuntimeInformation]::FrameworkDescription; powershell = $PSVersionTable.PSVersion.ToString()
        webView2Runtime = $wv; interactiveSession = [Environment]::UserInteractive
        ci = [ordered]@{ githubActions = ($env:GITHUB_ACTIONS -eq 'true'); runnerOs = $env:RUNNER_OS; runnerEnvironment = $env:RUNNER_ENVIRONMENT }
        build = [ordered]@{ exe = $exe; configuration = $Configuration; exeModifiedUtc = (Get-Item $exe).LastWriteTimeUtc.ToString('o'); exeBytes = (Get-Item $exe).Length; gitCommit = $sha; uncommittedFiles = $dirty }
        topProcessesByMemoryAtStart = @(Get-Process | Sort-Object WorkingSet64 -Descending | Select-Object -First 6 | ForEach-Object { $_.ProcessName })
        uptimeHours = [math]::Round(((Get-Date) - $os.LastBootUpTime).TotalHours, 1)
        gpuCounters = $null
        note = $script:GpuNote
    }
}
$environment = Get-Environment
$gpuAvailable = $true; try { $null = Get-Counter '\GPU Engine(*)\Utilization Percentage' -MaxSamples 1 -ErrorAction Stop } catch { $gpuAvailable = $false }
$environment.gpuCounters = if ($gpuAvailable) { 'available' } else { 'unavailable on this machine (GPU is reported as unavailable, not as zero)' }
$environment | ConvertTo-Json -Depth 6 | Set-Content "$out\environment.json" -Encoding utf8

# ---------------- helpers ----------------
function Get-ProcessMap([int]$rootPid) {
    # pid -> group/type for the app and every descendant, from the process table (parent links) and command lines.
    $all = @(Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId, Name, CommandLine)
    $map = @{}; $q = New-Object System.Collections.Generic.Queue[int]; $q.Enqueue($rootPid)
    $me = $all | Where-Object { $_.ProcessId -eq $rootPid }; if ($me) { $map[$rootPid] = @{ group = 'shell'; type = 'app' } }
    while ($q.Count) {
        $p = $q.Dequeue()
        foreach ($c in ($all | Where-Object { $_.ParentProcessId -eq $p })) {
            $id = [int]$c.ProcessId; if ($map.ContainsKey($id)) { continue }
            $type = if ($c.CommandLine -match '--type=([a-z-]+)') { $Matches[1] } else { 'browser' }
            $group = if ($c.Name -match '^msedgewebview2') { 'webview' } else { 'other' }
            $map[$id] = @{ group = $group; type = $type }; $q.Enqueue($id)
        }
    }
    $map
}

function Stop-Tree([int]$rootPid) {
    foreach ($id in @((Get-ProcessMap $rootPid).Keys)) { if ($id -ne $rootPid) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue } }
    Stop-Process -Id $rootPid -Force -ErrorAction SilentlyContinue
}

function Read-Json($path) { if (Test-Path $path) { try { Get-Content $path -Raw | ConvertFrom-Json } catch { $null } } else { $null } }

function Measure-Run([string]$scenario, [int]$runNumber) {
    $dataDir = Join-Path $out "data-$scenario-$runNumber"; New-Item -ItemType Directory -Path "$dataDir\benchmarks" -Force | Out-Null
    '{"firstRunDone":true}' | Set-Content "$dataDir\settings.json" -Encoding ascii
    $env:JEVBROWSE_DATA_DIR = $dataDir; $env:JEVBROWSE_NO_FILTER_UPDATE = '1'; $env:JEVBROWSE_AI = '0'; $env:JEVBROWSE_MODE = 'Simple'; $env:JEVBROWSE_DEVSPACE = '0'; $env:JEVBROWSE_THEME = 'dark'
    Remove-Item Env:\JEVBROWSE_START_URL -ErrorAction SilentlyContinue
    $appArgs = @(); $wantPage = 'jev://welcome'
    switch ($scenario) {
        'static' { $appArgs = @('--idle-scenario=static'); $env:JEVBROWSE_START_URL = 'jev://welcome/' }
        'panel-open' { $appArgs = @('--idle-scenario=panel-open'); $env:JEVBROWSE_START_URL = 'jev://welcome/' }
        'panel-closed' { $appArgs = @('--idle-scenario=panel-closed'); $env:JEVBROWSE_START_URL = 'jev://welcome/' }
        'agent-idle' { $appArgs = @('--agent-hidden-load=static:1'); $env:JEVBROWSE_START_URL = 'jev://welcome/' }
        'busy-control' { $env:JEVBROWSE_START_URL = $busyUrl; $wantPage = 'data:text/html' }
    }
    $run = [ordered]@{
        scenario = $scenario; run = $runNumber; startedAtUtc = (Get-Date).ToUniversalTime().ToString('o'); appExitedEarly = $false; windowFound = $false; windowMinimized = $false
        windowInForeground = $null; readErrors = 0; scenarioState = 'ok'; scenarioDetail = ''; pageMismatch = $null; processesAppearedWhileSampling = 0
        samples = @(); elapsedSeconds = 0; crossCheck = $null; systemCpuPercentMean = $null; scenarioStateReported = $null; addressBar = $null
    }
    $proc = if ($appArgs.Count) { Start-Process $exe -ArgumentList $appArgs -PassThru } else { Start-Process $exe -PassThru }
    try {
        $AE = [System.Windows.Automation.AutomationElement]; $Scope = [System.Windows.Automation.TreeScope]
        $win = $null
        for ($i = 0; $i -lt 60 -and -not $win -and -not $proc.HasExited; $i++) { $win = $AE::RootElement.FindFirst($Scope::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id))); if (-not $win) { Start-Sleep -Milliseconds 500 } }
        $run.windowFound = [bool]$win
        if ($win) {
            $h = [IntPtr]$win.Current.NativeWindowHandle
            $run.windowMinimized = [IdleWin]::IsIconic($h)
        }

        # Wait until the app says it is in the requested state (or, for the control, until its page is up), THEN warm up.
        $stateFile = if ($scenario -eq 'busy-control') { $null } elseif ($scenario -eq 'agent-idle') { "$dataDir\benchmarks\hidden-load-state.json" } else { "$dataDir\benchmarks\idle-scenario-state.json" }
        if ($stateFile) { for ($i = 0; $i -lt 180 -and -not (Test-Path $stateFile) -and -not $proc.HasExited; $i++) { Start-Sleep -Milliseconds 500 } }
        else { Start-Sleep -Seconds 12 }
        if ($proc.HasExited) { $run.appExitedEarly = $true; return [pscustomobject]$run }

        $state = if ($stateFile) { Read-Json $stateFile } else { $null }
        $run.scenarioStateReported = $state
        if ($stateFile) {
            if ($null -eq $state) { $run.scenarioState = 'missing' }
            else {
                $bad = @()
                switch ($scenario) {
                    'static' { if ($state.scenario -ne 'static' -or $state.panelOpen -or $state.restoreInFlight -or $state.agentSessionsRunning -gt 0) { $bad += 'not a quiet static window' } }
                    'panel-open' { if (-not $state.panelOpen -or $state.panelId -ne 'receipt') { $bad += 'the Receipt panel was not open' } }
                    'panel-closed' { if ($state.panelOpen) { $bad += 'the panel was still open' } }
                    'agent-idle' {
                        $pages = @($state.agentPages)
                        if ($pages.Count -ne 1 -or $pages[0].shown -ne 'Collapsed') { $bad += "expected one hidden agent page, found $($pages.Count)" }
                    }
                }
                if ($bad.Count) { $run.scenarioState = 'mismatch'; $run.scenarioDetail = $bad -join '; ' }
            }
        }

        # Which page is really showing? Read the address bar back, so a rejected start URL cannot pass as the page it was meant to be.
        try {
            $a = $win.FindFirst($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'AddressBox')))
            $run.addressBar = $a.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
        } catch { $run.addressBar = '(could not read)' }
        if ($scenario -ne 'agent-idle' -and $run.addressBar -notmatch [regex]::Escape($wantPage)) { $run.pageMismatch = "address bar shows '$($run.addressBar.Substring(0, [Math]::Min(60, $run.addressBar.Length)))'" }
        if ($scenario -eq 'busy-control' -and $run.addressBar.Length -gt 200) { $run.addressBar = $run.addressBar.Substring(0, 200) + '...' }
        $run.windowInForeground = ([IdleWin]::GetForegroundWindow() -eq $h)

        Start-Sleep -Seconds $WarmupSeconds
        if ($proc.HasExited) { $run.appExitedEarly = $true; return [pscustomobject]$run }

        # ---- sampling ----
        $map = Get-ProcessMap $proc.Id
        $prev = @{}; $first = @{}; $last = @{}; $sumDeltas = 0.0; $appeared = 0
        foreach ($id in @($map.Keys)) { try { $v = (Get-Process -Id $id -ErrorAction Stop).TotalProcessorTime.TotalSeconds; $prev[$id] = $v; $first[$id] = $v; $last[$id] = $v } catch { if (Get-Process -Id $id -ErrorAction SilentlyContinue) { $run.readErrors++ } } }
        $samples = New-Object System.Collections.Generic.List[object]; $sys = New-Object System.Collections.Generic.List[double]
        $n = [Math]::Max(1, [int]($SampleSeconds / $IntervalSeconds))
        $seenPids = @{}; foreach ($id in $map.Keys) { $seenPids[$id] = $map[$id] }
        $counterSets = [Math]::Max(1, [int][Math]::Floor($SampleSeconds / 7))
        $counterJob = Start-Job -ArgumentList $counterSets, $gpuAvailable -ScriptBlock {
            param($k, $gpuOn)
            $paths = @('\Processor(_Total)\% Processor Time'); if ($gpuOn) { $paths = @('\GPU Engine(*)\Utilization Percentage') + $paths }
            # Job output is serialised, which flattens counter objects to strings, so hand back plain records.
            Get-Counter $paths -SampleInterval 2 -MaxSamples $k -ErrorAction SilentlyContinue | ForEach-Object {
                , @($_.CounterSamples | ForEach-Object { [pscustomobject]@{ path = $_.Path; instance = $_.InstanceName; value = [double]$_.CookedValue } })
            }
        }
        $sw = [Diagnostics.Stopwatch]::StartNew(); $lastT = 0.0
        for ($i = 0; $i -lt $n; $i++) {
            $wait = ($i + 1) * $IntervalSeconds - $sw.Elapsed.TotalSeconds   # keep a fixed cadence: the work below takes a fraction of a second
            if ($wait -gt 0) { Start-Sleep -Milliseconds ([int]($wait * 1000)) }
            if ($proc.HasExited) { $run.appExitedEarly = $true; break }
            $map = Get-ProcessMap $proc.Id
            $now = $sw.Elapsed.TotalSeconds; $dt = $now - $lastT; $lastT = $now
            $cpu = @{ shell = 0.0; webview = 0.0; other = 0.0 }; $perProc = @()
            foreach ($id in @($map.Keys)) {
                try {
                    $t = (Get-Process -Id $id -ErrorAction Stop).TotalProcessorTime.TotalSeconds
                    $d = 0.0
                    if ($prev.ContainsKey($id)) { $d = $t - $prev[$id]; if ($d -lt 0) { $d = 0.0 } } else { $first[$id] = $t; $appeared++ }
                    $prev[$id] = $t; $last[$id] = $t
                    $cpu[$map[$id].group] += $d; $sumDeltas += $d
                    $perProc += [ordered]@{ pid = $id; group = $map[$id].group; type = $map[$id].type; cpuSeconds = [math]::Round($d, 4) }
                } catch { if (Get-Process -Id $id -ErrorAction SilentlyContinue) { $run.readErrors++ } }
            }
            foreach ($id in $map.Keys) { $seenPids[$id] = $map[$id] }
            $samples.Add([pscustomobject]@{
                    t = [math]::Round($now, 2); dt = [math]::Round($dt, 2)
                    shellCpuPercent = [math]::Round(100 * $cpu.shell / $dt, 3); webviewCpuPercent = [math]::Round(100 * $cpu.webview / $dt, 3)
                    totalCpuPercent = [math]::Round(100 * ($cpu.shell + $cpu.webview + $cpu.other) / $dt, 3)
                    processes = $perProc.Count; perProcess = $perProc
                })
        }
        $run.samples = $samples.ToArray(); $run.elapsedSeconds = [math]::Round($sw.Elapsed.TotalSeconds, 2); $run.processesAppearedWhileSampling = $appeared

        # GPU and whole-machine CPU come from the background counter job that ran alongside (the GPU counter query alone takes ~5 s).
        $gpuSamples = New-Object System.Collections.Generic.List[object]
        if ($counterJob) {
            $sets = @(Receive-Job $counterJob -Wait -AutoRemoveJob -ErrorAction SilentlyContinue)
            foreach ($set in $sets) {
                $gs = 0.0; $gw = 0.0; $sysNow = $null; $gpuRows = 0
                foreach ($cs in @($set)) {
                    if ($cs.path -match 'processor\(_total\)') { $sysNow = $cs.value }
                    elseif ($cs.instance -match '^pid_(\d+)_') { $gpuRows++; $p2 = [int]$Matches[1]; if ($seenPids.ContainsKey($p2)) { if ($seenPids[$p2].group -eq 'shell') { $gs += $cs.value } elseif ($seenPids[$p2].group -eq 'webview') { $gw += $cs.value } } }
                }
                if ($null -ne $sysNow) { $sys.Add($sysNow) }
                # No GPU rows at all means the counter gave nothing this time: that is "unavailable", never "zero".
                if ($gpuAvailable -and $gpuRows -gt 0) { $gpuSamples.Add([pscustomobject]@{ shellGpuPercent = [math]::Round($gs, 3); webviewGpuPercent = [math]::Round($gw, 3) }) }
            }
        }
        $run.gpuSamples = $gpuSamples.ToArray()
        $ft = 0.0; foreach ($id in @($last.Keys)) { if ($first.ContainsKey($id)) { $ft += ($last[$id] - $first[$id]) } }
        $run.crossCheck = [ordered]@{ perSampleSeconds = [math]::Round($sumDeltas, 3); firstToLastSeconds = [math]::Round($ft, 3) }
        if ($sys.Count) { $run.systemCpuPercentMean = [math]::Round(($sys | Measure-Object -Average).Average, 2) }
        $run.processesAtEnd = $map.Count
    }
    finally { Stop-Tree $proc.Id }
    [pscustomobject]$run
}

# ---------------- the suite ----------------
$suite = [ordered]@{}
foreach ($scenario in $Scenarios) {
    $runsForScenario = @()
    for ($r = 1; $r -le $Runs; $r++) {
        Write-Host "[$scenario] run $r of $Runs ..."
        $raw = Measure-Run $scenario $r
        $problems = @(Test-RunValidity $raw $plan)
        $verdict = if ($problems.Count -eq 0) { 'valid' } else { 'inconclusive' }
        $totals = if ($raw.samples.Count) { Get-RunTotals $raw } else { [ordered]@{} }
        $raw | Add-Member -NotePropertyName verdict -NotePropertyValue $verdict -Force
        $raw | Add-Member -NotePropertyName problems -NotePropertyValue $problems -Force
        $raw | Add-Member -NotePropertyName totals -NotePropertyValue $totals -Force
        $raw | ConvertTo-Json -Depth 8 | Set-Content "$out\raw\$scenario-run$r.json" -Encoding utf8
        $runsForScenario += [pscustomobject]@{ run = $r; verdict = $verdict; problems = $problems; totals = $totals; windowInForeground = $raw.windowInForeground }
        Write-Host ("    {0}: shell CPU {1}%  webview CPU {2}%  | GPU shell {3}%  webview {4}%  {5}" -f $verdict, $totals.shellCpuMean, $totals.webviewCpuMean, $totals.shellGpuMean, $totals.webviewGpuMean, (@($problems) -join '; '))
    }
    $suite[$scenario] = [ordered]@{ summary = Get-ScenarioSummary $runsForScenario $plan; runs = $runsForScenario }
}

# The positive control: the measurement must SEE a busy page, or nothing else in this result means anything.
$control = $null
if ($suite.Contains('busy-control')) {
    $busy = $suite['busy-control'].summary.totalCpuMean
    $quiet = if ($suite.Contains('static')) { $suite['static'].summary.totalCpuMean } else { $null }
    $seen = ($null -ne $busy -and $busy.median -gt 10 -and ($null -eq $quiet -or $busy.median -gt $quiet.median + 10))
    $control = [ordered]@{ busyTotalCpuMedian = $(if ($busy) { $busy.median } else { $null }); staticTotalCpuMedian = $(if ($quiet) { $quiet.median } else { $null }); detected = $seen
        meaning = 'The deliberately busy page must read more than 10 points of CPU above the static page. If it does not, the instrument cannot see activity and this result set is inconclusive.' }
}

$anyInconclusive = @($suite.Values | Where-Object { $_.summary.runsInconclusive -gt 0 }).Count -gt 0
$controlFailed = ($null -ne $control -and -not $control.detected)
$comparison = $null
if ($CompareTo) {
    $base = Read-Json $CompareTo
    if ($base) { $comparison = [ordered]@{}; foreach ($sc in $Scenarios) { if ($base.scenarios.PSObject.Properties.Name -contains $sc) { $comparison[$sc] = Compare-Scenario $suite[$sc].summary $base.scenarios.$sc.summary } } }
}
$verdict = if ($controlFailed) { 'inconclusive: the positive control was not detected' } elseif ($anyInconclusive) { 'contains inconclusive runs' } else { 'all runs valid' }
$results = [ordered]@{ label = $Label; verdict = $verdict; plan = $plan; environment = $environment; positiveControl = $control; scenarios = $suite; comparison = $comparison; note = $script:GpuNote }
$results | ConvertTo-Json -Depth 10 | Set-Content "$out\results.json" -Encoding utf8

# ---------------- summary.md ----------------
$md = New-Object System.Collections.Generic.List[string]
$md.Add("# Idle cost: $(if ($Label) { $Label } else { $stamp })"); $md.Add('')
$md.Add("Verdict: **$verdict**. $($environment.os), $($environment.cpuModel), $($environment.logicalCores) logical cores; GPU counters $($environment.gpuCounters). Build: $($environment.build.configuration), commit $($environment.build.gitCommit), $($environment.build.uncommittedFiles) uncommitted files.")
$md.Add(''); $md.Add("Plan: $Runs runs per scenario, warm-up $WarmupSeconds s, sampling $SampleSeconds s every $IntervalSeconds s. CPU = percent of ONE core, medians over valid runs (min to max in brackets). GPU = summed engine utilisation, reported separately."); $md.Add('')
$md.Add('| Scenario | valid / attempted | Shell CPU % | WebView2 CPU % | Shell GPU % | WebView2 GPU % |'); $md.Add('|---|---|---|---|---|---|')
function Cell($st) { if ($null -eq $st) { 'n/a' } else { "$($st.median) ($($st.min) to $($st.max))" } }
foreach ($sc in $Scenarios) { $s = $suite[$sc].summary; $md.Add("| $sc | $($s.runsValid) / $($s.runsAttempted) | $(Cell $s.shellCpuMean) | $(Cell $s.webviewCpuMean) | $(Cell $s.shellGpuMean) | $(Cell $s.webviewGpuMean) |") }
if ($control) { $md.Add(''); $md.Add("Positive control: $(if ($control.detected) { 'DETECTED' } else { 'NOT DETECTED' }) (busy page total CPU median $($control.busyTotalCpuMedian) vs static $($control.staticTotalCpuMedian))") }
$reasons = @($suite.Values | ForEach-Object { $_.summary.inconclusiveReasons } | Select-Object -Unique)
if ($reasons.Count) { $md.Add(''); $md.Add('Inconclusive runs, and why:'); foreach ($x in $reasons) { $md.Add("- $x") } }
if ($comparison) { $md.Add(''); $md.Add('Comparison with the baseline result set (shell CPU):'); foreach ($k in $comparison.Keys) { $md.Add("- **$k**: $($comparison[$k].verdict). $($comparison[$k].reason)") } }
$md.Add(''); $md.Add("_${script:GpuNote}_")
$md | Set-Content "$out\summary.md" -Encoding utf8
$md | ForEach-Object { Write-Host $_ }
Write-Host "Raw results and environment: $out"

if ($env:GITHUB_STEP_SUMMARY) { $md | Add-Content $env:GITHUB_STEP_SUMMARY }
$code = 0
if (-not $ReportOnly -and ($anyInconclusive -or $controlFailed)) { $code = 2 }
if ($Enforce -and $comparison -and (@($comparison.Values | Where-Object { $_.verdict -eq 'regression' }).Count -gt 0)) { $code = 1 }
"OUT=$out" | Out-File "$out\out-path.txt" -Encoding ascii
exit $code
