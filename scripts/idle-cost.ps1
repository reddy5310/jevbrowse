<#
.SYNOPSIS
  Measures what the app costs while it is doing nothing: CPU and GPU used by the app and its child processes at rest.

.DESCRIPTION
  P2 adds depth, light and motion to the interface. The target is "no added continuous rendering at idle": this measures
  it, so the claim is a number taken before and after rather than a promise.

  Idle means: a window is open on the local welcome page (no network), nobody touches it, nothing is loading. It samples for a fixed
  period and reports the average and the peak. CPU is reported as a percentage of ONE core (so 100 = one core fully
  busy) and GPU as the sum of utilisation across the GPU engines that belong to the app's processes.

  It only ever measures and stops the process tree it launched. It creates a new uniquely named directory under a fixed
  test root and never deletes anything. It does not touch any other program.

  Limits, stated plainly: one machine, one GPU driver, a debug build unless -Configuration release. GPU counters are the
  Windows "GPU Engine" counters; a machine without them reports GPU as unavailable rather than as zero. A window that is
  minimised or occluded may cost less than one in front, and this measures one in front.

.PARAMETER Configuration   debug (default) or release.
.PARAMETER Root            Fixed test root; each run gets a new subdirectory.
.PARAMETER SettleSeconds   Time to let start-up work finish before sampling.
.PARAMETER SampleSeconds   How long to sample.
.PARAMETER Label           Names the run in the output (for example "before" or "after").
.PARAMETER StartUrl        Page to open. Defaults to the LOCAL welcome page (jev://welcome/) so the baseline never depends on a website.
                          Used with a data: URL to load a deliberately animated page as a positive control.
#>
param(
    [string]$Configuration = 'debug',
    [string]$Root = 'D:\Browser\_ui-check',
    [int]$SettleSeconds = 20,
    [int]$SampleSeconds = 40,
    [string]$Label = 'run',
    [string]$StartUrl = 'jev://welcome/',
    # Agent pages held open while measuring: 'static:N', 'busy:N' (hidden, and deliberately hard-working), or 'busy-shown:N' (the control:
    # the same page SHOWN, which proves the instrument can see a busy page). Empty = none.
    [string]$AgentLoad = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ProcessScope.ps1')
. (Join-Path $PSScriptRoot 'env.ps1') | Out-Null

$rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\')
if ($rootFull.Length -lt 8 -or $rootFull -match '^[A-Za-z]:$') { throw "Refusing test root '$rootFull'." }
New-Item -ItemType Directory -Force $rootFull | Out-Null
$Out = [IO.Path]::GetFullPath((Join-Path $rootFull ('idle-{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), ([guid]::NewGuid().ToString('N').Substring(0, 8)))))
if ((Split-Path $Out -Parent) -ne $rootFull -or (Test-Path -LiteralPath $Out)) { throw "Unsafe or existing run directory '$Out'." }
New-Item -ItemType Directory $Out | Out-Null

$env:JEVBROWSE_DATA_DIR = $Out
$env:JEVBROWSE_NO_FILTER_UPDATE = '1'; $env:JEVBROWSE_AI = '0'; $env:JEVBROWSE_MODE = 'Simple'; $env:JEVBROWSE_DEVSPACE = '0'
'{"firstRunDone":true}' | Set-Content "$Out\settings.json" -Encoding ascii
if ($StartUrl -match '^data:text/html,(.*)$') { $StartUrl = 'data:text/html,' + [Uri]::EscapeDataString($Matches[1]) }
if ($StartUrl) { $env:JEVBROWSE_START_URL = $StartUrl } else { Remove-Item Env:\JEVBROWSE_START_URL -ErrorAction SilentlyContinue }

function Get-Descendants([int]$rootPid) {
    $all = @(Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId)
    $found = New-Object System.Collections.Generic.List[int]
    $q = New-Object System.Collections.Generic.Queue[int]; $q.Enqueue($rootPid)
    while ($q.Count -gt 0) {
        $p = $q.Dequeue()
        foreach ($c in ($all | Where-Object { $_.ParentProcessId -eq $p })) { if (-not $found.Contains([int]$c.ProcessId)) { $found.Add([int]$c.ProcessId); $q.Enqueue([int]$c.ProcessId) } }
    }
    $found
}

$exe = Join-Path $PSScriptRoot "..\artifacts\bin\JevBrowse.App\${Configuration}_win-x64\JevBrowse.App.exe"
$proc = if ($AgentLoad) { Start-Process (Resolve-Path $exe) -ArgumentList "--agent-hidden-load=$AgentLoad" -PassThru } else { Start-Process (Resolve-Path $exe) -PassThru }
$cores = [Environment]::ProcessorCount
try {
    Start-Sleep -Seconds $SettleSeconds

    # Which page is really showing? Read the address bar back through UI Automation, so a start URL that was rejected and
    # replaced by the welcome page cannot masquerade as the page it was meant to be.
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $AE = [System.Windows.Automation.AutomationElement]; $Scope = [System.Windows.Automation.TreeScope]
    $addressShows = '(could not read)'
    try {
        $w = $AE::RootElement.FindFirst($Scope::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id)))
        $a = $w.FindFirst($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'AddressBox')))
        $addressShows = $a.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    } catch { }

    $gpuAvailable = $true
    try { $null = Get-Counter '\GPU Engine(*)\Utilization Percentage' -MaxSamples 1 -ErrorAction Stop } catch { $gpuAvailable = $false }

    $interval = 2
    $n = [Math]::Max(1, [int]($SampleSeconds / $interval))
    $prevCpu = @{}
    $firstCpu = @{}; $lastCpu = @{}      # independent cross-check: total CPU consumed, from first and last reading only
    $readErrors = 0                      # a process that is STILL RUNNING but could not be read: a real error
    $exitedDuringSampling = 0            # a process that no longer exists by the time it was read: normal for helper processes
    $gpuMatchedMax = 0
    $sumDeltas = 0.0                     # CPU-seconds added up sample by sample
    $cpuSamples = New-Object System.Collections.Generic.List[double]
    $gpuSamples = New-Object System.Collections.Generic.List[double]
    $procCounts = New-Object System.Collections.Generic.List[int]
    $wsSamples = New-Object System.Collections.Generic.List[double]; $privSamples = New-Object System.Collections.Generic.List[double]   # MB, summed over the app and its children
    $sw = [Diagnostics.Stopwatch]::StartNew(); $lastT = 0.0

    # Prime the CPU baseline so the first sample is a real delta.
    foreach ($id in @($proc.Id) + @(Get-Descendants $proc.Id)) {
        try { $v = (Get-Process -Id $id -ErrorAction Stop).TotalProcessorTime.TotalSeconds; $prevCpu[$id] = $v; $firstCpu[$id] = $v; $lastCpu[$id] = $v } catch { if (Get-Process -Id $id -ErrorAction SilentlyContinue) { $readErrors++ } else { $exitedDuringSampling++ } }
    }
    $lastT = $sw.Elapsed.TotalSeconds

    for ($i = 0; $i -lt $n; $i++) {
        Start-Sleep -Seconds $interval
        $ids = @($proc.Id) + @(Get-Descendants $proc.Id)
        $procCounts.Add($ids.Count)
        $now = $sw.Elapsed.TotalSeconds; $dt = $now - $lastT; $lastT = $now

        $cpuSec = 0.0; $wsMb = 0.0; $privMb = 0.0
        foreach ($id in $ids) {
            try {
                $gp = Get-Process -Id $id -ErrorAction Stop; $t = $gp.TotalProcessorTime.TotalSeconds; $wsMb += $gp.WorkingSet64 / 1MB; $privMb += $gp.PrivateMemorySize64 / 1MB
                # Plain comparison, not [Math]::Max(0, $double): that call is ambiguous in PowerShell and throws, and an
                # empty catch turned every sample into a silent zero.
                if ($prevCpu.ContainsKey($id)) { $d = $t - $prevCpu[$id]; if ($d -gt 0) { $cpuSec += $d } }
                else { $firstCpu[$id] = $t }        # a process that appeared during sampling
                $prevCpu[$id] = $t; $lastCpu[$id] = $t
            } catch { if (Get-Process -Id $id -ErrorAction SilentlyContinue) { $readErrors++ } else { $exitedDuringSampling++ } }
        }
        $sumDeltas += $cpuSec; $wsSamples.Add($wsMb); $privSamples.Add($privMb)
        $cpuSamples.Add(100.0 * $cpuSec / $dt)   # % of ONE core, over the REAL elapsed time (listing the tree is not instant)

        if ($gpuAvailable) {
            $set = @{}; foreach ($id in $ids) { $set["pid_$id"] = $true }
            $g = 0.0; $matched = 0
            try {
                (Get-Counter '\GPU Engine(*)\Utilization Percentage' -ErrorAction Stop).CounterSamples | ForEach-Object {
                    if ($_.InstanceName -match '^(pid_\d+)_' -and $set.ContainsKey($Matches[1])) { $g += $_.CookedValue; $matched++ }
                }
            } catch { }
            $gpuSamples.Add($g); if ($matched -gt $gpuMatchedMax) { $gpuMatchedMax = $matched }
        }
    }

    # What the app says the agent pages' controls were doing, so "hidden" is read back rather than assumed.
    $hiddenState = $null
    if ($AgentLoad) { $hs = Join-Path $Out 'benchmarks\hidden-load-state.json'; if (Test-Path $hs) { $hiddenState = Get-Content $hs -Raw | ConvertFrom-Json } }
    $crossCheck = 0.0
    foreach ($id in $lastCpu.Keys) { if ($firstCpu.ContainsKey($id)) { $crossCheck += ($lastCpu[$id] - $firstCpu[$id]) } }
    $summed = $sumDeltas
    $trust = if ($readErrors -gt 0) { "UNRELIABLE: $readErrors process readings failed" }
             elseif ([Math]::Abs($crossCheck - $summed) -gt [Math]::Max(0.25, 0.25 * $crossCheck)) { "UNRELIABLE: per-sample CPU ($([Math]::Round($summed,2)) s) disagrees with first-to-last CPU ($([Math]::Round($crossCheck,2)) s)" }
             else { 'ok' }

    function Stat($xs) { if ($xs.Count -eq 0) { return $null }; $m = $xs | Measure-Object -Average -Maximum; [ordered]@{ average = [Math]::Round($m.Average, 2); peak = [Math]::Round($m.Maximum, 2) } }
    $report = [ordered]@{
        label = $Label
        configuration = $Configuration
        sampledSeconds = $n * $interval
        logicalCores = $cores
        processesInTree = [ordered]@{ min = ($procCounts | Measure-Object -Minimum).Minimum; max = ($procCounts | Measure-Object -Maximum).Maximum }
        measurementTrust = $trust
        processesThatExitedWhileSampling = $exitedDuringSampling
        addressBarShows = if ($addressShows.Length -gt 60) { $addressShows.Substring(0, 60) + '...' } else { $addressShows }
        cpuSecondsConsumedWhileSampling = [Math]::Round($crossCheck, 3)
        cpuPercentOfOneCore = Stat $cpuSamples
        memoryMegabytes = [ordered]@{ workingSetSummedOverTheProcessTree = (Stat $wsSamples); privateBytesSummedOverTheProcessTree = (Stat $privSamples) }
        agentLoad = $(if ($AgentLoad) { $AgentLoad } else { 'none' })
        agentPagesAsTheAppReportedThem = $hiddenState
        gpuEngineInstancesMatchedToApp = $gpuMatchedMax
        gpuPercentSummedAcrossEngines = if ($gpuAvailable) { Stat $gpuSamples } else { 'unavailable on this machine' }
        note = 'Idle = window in front on the local welcome page, untouched. One machine; not a comparison with any other browser.'
    }
    $report | ConvertTo-Json -Depth 4 | Set-Content "$Out\idle-cost.json" -Encoding utf8
    $report | ConvertTo-Json -Depth 4
    # The address bar shows the URL partly decoded, so compare only the part before any query, fragment or payload.
    $wantHead = ($StartUrl -split '[?#,]')[0]
    if ($StartUrl -and -not $addressShows.StartsWith($wantHead, [StringComparison]::OrdinalIgnoreCase)) {
        Write-Output "WARNING: -StartUrl was not the page that loaded (address bar shows '$addressShows'); this is not the control it was meant to be."
        exit 2
    }
    if ($AgentLoad) {
        $kind = ($AgentLoad -split ':')[0]
        if ($null -eq $hiddenState) { Write-Output 'WARNING: the app never reported its agent pages, so this run does not show what it was meant to.'; exit 2 }
        $shownCount = @($hiddenState.agentPages | Where-Object { $_.shown -eq 'Visible' }).Count
        if ($kind -ne 'busy-shown' -and $shownCount -gt 0) { Write-Output "WARNING: $shownCount agent page(s) were SHOWN in a run meant to keep them hidden."; exit 2 }
        if ($kind -eq 'busy-shown' -and $shownCount -lt 1) { Write-Output 'WARNING: the control page was not shown, so it is not the control it was meant to be.'; exit 2 }
    }
    if ($trust -ne 'ok') { exit 2 }
}
finally {
    # Children first, then the app: only what THIS script started.
    Stop-OwnedProcesses (Get-OwnedProcesses $proc)   # id AND start time (scripts\ProcessScope.ps1)
}
