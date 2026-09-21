<#
.SYNOPSIS
  Real-app recovery check. Starts the real app with a normal tab and a LIVE Private session (a tab with a cookie in its throwaway profile), then either
  KILLS it (TerminateProcess, as a crash or a forced logoff would) or CLOSES it normally, and inspects what was left on disk. For the crash it then starts
  the app again and checks that it recovers.

  Checks, per scenario:
    crash:           the throwaway profile is still there after the kill (a crash cannot clean up: recorded, not hidden); no Private URL, marker or cookie
                     value is in browser.db, its WAL, or thumbnails; the database copy passes integrity_check; the NEXT start sweeps the leftover profile
                     and its lock; the normal tab is still in the database and restored; the Private workspace and its tab are gone; integrity_check again.
    clean-shutdown:  the app exits by itself within 40 s; no profile is left behind; nothing of the Private session is on disk; no app or WebView2 process
                     from the run survives.

  Inconclusive (exit 2) when the app never reached the seeded state, or a check could not be made. Only ever kills the process tree it started; every data
  directory is new and unique under -Root; nothing is deleted. Does not change Windows settings.
#>
param(
    [ValidateSet('crash', 'clean-shutdown', 'corrupt-db', 'all')][string]$Scenario = 'all',
    [string]$Configuration = 'debug',
    [string]$ExePath = '',
    [string]$Root = 'D:\Browser\_ui-check\recovery'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class RecWin { [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h); }
'@
$rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\')
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
if ($Root -notmatch '^[A-Za-z]:[\\/]' -or $rootFull.Length -lt 8 -or $rootFull.StartsWith($repoRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing test root '$Root'." }
$exe = if ($ExePath) { $ExePath } else { Join-Path $PSScriptRoot "..\artifacts\bin\JevBrowse.App\${Configuration}_win-x64\JevBrowse.App.exe" }
if (-not (Test-Path $exe)) { throw "No app at '$exe'." }
$exe = (Resolve-Path $exe).Path
$run = Join-Path $rootFull ("{0}-{1}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), [guid]::NewGuid().ToString('N').Substring(0, 6))
New-Item -ItemType Directory -Path $run -Force | Out-Null

$py = @'
import sqlite3, sys, json
p = sys.argv[1]
c = sqlite3.connect(p)
out = {"integrity": c.execute("PRAGMA integrity_check").fetchone()[0], "user_version": c.execute("PRAGMA user_version").fetchone()[0]}
out["tabs"] = [r[0] for r in c.execute("SELECT url FROM tabs")]
out["workspaces"] = [r[0] for r in c.execute("SELECT name FROM workspaces")]
print(json.dumps(out))
'@
$pyFile = Join-Path $run 'dbcheck.py'; $py | Set-Content $pyFile -Encoding utf8
$havePython = [bool](Get-Command python -ErrorAction SilentlyContinue)

$results = [ordered]@{}
$inconclusive = $false
function Add-Check($scenario, $name, $ok, $detail = '') { if (-not $results.Contains($scenario)) { $results[$scenario] = [ordered]@{} }; $results[$scenario][$name] = [ordered]@{ pass = [bool]$ok; detail = $detail }; Write-Host ("  {0,-4} {1} {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail) }

function Get-Tree([int]$rootPid) {
    $all = @(Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId)
    $ids = New-Object System.Collections.Generic.List[int]; $q = New-Object System.Collections.Generic.Queue[int]; $q.Enqueue($rootPid)
    while ($q.Count) { $p = $q.Dequeue(); foreach ($c in ($all | Where-Object { $_.ParentProcessId -eq $p })) { $ids.Add([int]$c.ProcessId); $q.Enqueue([int]$c.ProcessId) } }
    $ids
}
function Start-App($data, [string[]]$appArgs, $extraEnv = @{}) {
    $env:JEVBROWSE_DATA_DIR = $data; $env:JEVBROWSE_NO_FILTER_UPDATE = '1'; $env:JEVBROWSE_AI = '0'; $env:JEVBROWSE_MODE = 'Simple'; $env:JEVBROWSE_DEVSPACE = '0'
    Remove-Item Env:\JEVBROWSE_START_URL -ErrorAction SilentlyContinue
    if ($appArgs.Count) { Start-Process $exe -ArgumentList $appArgs -PassThru } else { Start-Process $exe -PassThru }
}
function Find-Window($proc) {
    $AE = [System.Windows.Automation.AutomationElement]
    for ($i = 0; $i -lt 60 -and -not $proc.HasExited; $i++) {
        $w = $AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id)))
        if ($w) { return $w }; Start-Sleep -Milliseconds 500
    }
    $null
}
function Db-Copy($data, $dest) {
    New-Item -ItemType Directory -Path $dest -Force | Out-Null
    foreach ($f in 'browser.db', 'browser.db-wal', 'browser.db-shm') { $s = Get-ChildItem $data -Recurse -Filter $f -File -ErrorAction SilentlyContinue | Select-Object -First 1; if ($s) { Copy-Item $s.FullName "$dest\$f" } }
    "$dest\browser.db"
}
function Db-Report($copy) {
    if (-not $havePython -or -not (Test-Path $copy)) { return $null }
    try { (python $pyFile $copy | ConvertFrom-Json) } catch { $null }
}
function Bytes-Contain($dir, [string[]]$needles) {
    # Any file under $dir (database, WAL, thumbnails, ...) containing one of the needles as raw bytes (ASCII or UTF-16).
    $hits = @()
    foreach ($f in (Get-ChildItem $dir -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch '\\profiles\\' })) {
        try { $b = [IO.File]::ReadAllBytes($f.FullName) } catch { continue }
        $t = [Text.Encoding]::GetEncoding(28591).GetString($b)
        foreach ($n in $needles) { if ($t.Contains($n) -or $t.Contains(($n.ToCharArray() -join "`0"))) { $hits += "$($f.Name): $n" } }
    }
    $hits
}
function Stop-Leftovers($ids) { foreach ($id in $ids) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue } }

function Seed-App($name) {
    $data = Join-Path $run "data-$name"; New-Item -ItemType Directory -Path "$data\benchmarks" -Force | Out-Null
    '{"firstRunDone":true}' | Set-Content "$data\settings.json" -Encoding ascii
    $p = Start-App $data @('--recovery-seed')
    $win = Find-Window $p
    $seed = "$data\benchmarks\recovery-seed.json"
    for ($i = 0; $i -lt 120 -and -not (Test-Path $seed) -and -not $p.HasExited; $i++) { Start-Sleep -Milliseconds 500 }
    if (-not (Test-Path $seed)) { return [pscustomobject]@{ ok = $false; data = $data; proc = $p; win = $win; seed = $null } }
    [pscustomobject]@{ ok = $true; data = $data; proc = $p; win = $win; seed = (Get-Content $seed -Raw | ConvertFrom-Json) }
}
$markers = @('SECRET-7731', 'private-marker', 'private-cookie-7731')

function Test-Crash {
    Write-Host '[crash] seeding a normal tab and a live Private session, then killing the app'
    $s = Seed-App 'crash'
    if (-not $s.ok) { Add-Check 'crash' 'seed reached' $false 'the app never reached the seeded state'; $script:inconclusive = $true; if (-not $s.proc.HasExited) { Stop-Leftovers (@(Get-Tree $s.proc.Id) + $s.proc.Id) }; return }
    $eph = Join-Path $s.data 'profiles\ephemeral'
    $before = @(Get-ChildItem $eph -Directory -ErrorAction SilentlyContinue)
    Add-Check 'crash' 'a Private profile exists while the session is live' ($before.Count -ge 1) "$($before.Count) profile dir(s)"
    $tree = @(Get-Tree $s.proc.Id)
    Stop-Process -Id $s.proc.Id -Force            # TerminateProcess: no shutdown path runs
    $s.proc.WaitForExit(10000) | Out-Null
    # The engine's own processes notice the host is gone and exit on their own. Measure how long that takes; they hold the throwaway profile until they do.
    $waited = 0; $stragglers = @($tree | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue })
    while ($stragglers.Count -and $waited -lt 30) { Start-Sleep -Seconds 1; $waited++; $stragglers = @($tree | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue }) }
    if ($stragglers.Count) {
        # Keep the evidence: what were they, which profile did they belong to, and how old were they?
        $detail = @(Get-CimInstance Win32_Process | Where-Object { $stragglers -contains [int]$_.ProcessId } | ForEach-Object { [ordered]@{ pid = $_.ProcessId; parent = $_.ParentProcessId; name = $_.Name; created = $_.CreationDate.ToString('o'); type = $(if ($_.CommandLine -match '--type=([a-z-]+)') { $Matches[1] } else { 'browser' }); userDataDir = $(if ($_.CommandLine -match '--user-data-dir="?([^" ]+)') { $Matches[1] } else { $null }) } })
        $detail | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $run 'stragglers.json') -Encoding utf8
        $script:stragglerSummary = ($detail | Group-Object { "$($_.type) [$($_.userDataDir)]" } | ForEach-Object { "$($_.Count) x $($_.Name)" }) -join '; '
    }
    if ($stragglers.Count) { Stop-Leftovers $stragglers; Start-Sleep 2 }
    Add-Check 'crash' 'engine processes exit on their own after the app is killed (within 30 s)' ($stragglers.Count -eq 0) "$($tree.Count) processes in the tree; all gone after ~$waited s; $($stragglers.Count) had to be stopped $script:stragglerSummary"

    $left = @(Get-ChildItem $eph -Directory -ErrorAction SilentlyContinue)
    Add-Check 'crash' 'the crash left the throwaway profile on disk (expected: a crash cannot clean up)' ($left.Count -ge 1) "$($left.Count) dir(s)"
    $hits = @(Bytes-Contain $s.data $markers)
    Add-Check 'crash' 'no Private URL, marker or cookie value is in browser.db, its WAL, thumbnails or anything outside the throwaway profile' ($hits.Count -eq 0) ($hits -join '; ')
    $copy = Db-Copy $s.data (Join-Path $run 'dbcopy-crash-1')
    $r1 = Db-Report $copy
    if ($null -eq $r1) { Add-Check 'crash' 'database inspected after the kill' $false 'python or the database copy is missing'; $script:inconclusive = $true }
    else {
        Add-Check 'crash' 'database passes integrity_check after the kill (checked on a copy, so the app is not helped)' ($r1.integrity -eq 'ok') "user_version $($r1.user_version)"
        Add-Check 'crash' 'the normal tab is in the database' (@($r1.tabs | Where-Object { $_ -match 'normal-marker=keep-4242' }).Count -eq 1)
        Add-Check 'crash' 'no Private tab row exists' (@($r1.tabs | Where-Object { $_ -match 'private-marker' }).Count -eq 0)
    }

    Write-Host '[crash] starting the app again'
    $p2 = Start-App $s.data @()
    $win2 = Find-Window $p2
    Start-Sleep -Seconds 15
    Add-Check 'crash' 'the app starts again after the crash' ($win2 -and -not $p2.HasExited)
    $after = @(Get-ChildItem $eph -Directory -ErrorAction SilentlyContinue)
    $locks = @(Get-ChildItem $eph -Filter '*.lock' -File -ErrorAction SilentlyContinue)
    Add-Check 'crash' 'the next start swept the leftover profile and its lock (only the new session profile of this run may exist)' (($after | Where-Object { $left.Name -contains $_.Name }).Count -eq 0 -and -not ($locks | Where-Object { $left.Name -contains ($_.BaseName) })) "left before: $($left.Count), same names now: $(@($after | Where-Object { $left.Name -contains $_.Name }).Count)"
    $tree2 = @(Get-Tree $p2.Id)
    $null = $p2.CloseMainWindow(); if (-not $p2.WaitForExit(40000)) { Stop-Process -Id $p2.Id -Force; Add-Check 'crash' 'the restarted app closes normally' $false 'had to be killed' } else { Add-Check 'crash' 'the restarted app closes normally' $true }
    Start-Sleep 3; Stop-Leftovers @($tree2 | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue })
    $copy2 = Db-Copy $s.data (Join-Path $run 'dbcopy-crash-2')
    $r2 = Db-Report $copy2
    if ($null -eq $r2) { $script:inconclusive = $true } else {
        Add-Check 'crash' 'integrity_check passes after recovery and a normal close' ($r2.integrity -eq 'ok')
        Add-Check 'crash' 'the normal tab survived crash, restart and close' (@($r2.tabs | Where-Object { $_ -match 'normal-marker=keep-4242' }).Count -eq 1)
    }
    $hits2 = @(Bytes-Contain $s.data $markers)
    Add-Check 'crash' 'still nothing of the Private session on disk outside the profile folder after recovery' ($hits2.Count -eq 0) ($hits2 -join '; ')
    $eph2 = @(Get-ChildItem $eph -Directory -ErrorAction SilentlyContinue)
    Add-Check 'crash' 'no throwaway profile remains after the recovered app closed' ($eph2.Count -eq 0) "$($eph2.Count) dir(s)"
}

function Test-CleanShutdown {
    Write-Host '[clean-shutdown] seeding, then closing the window with a live Private session'
    $s = Seed-App 'clean'
    if (-not $s.ok) { Add-Check 'clean-shutdown' 'seed reached' $false 'the app never reached the seeded state'; $script:inconclusive = $true; if (-not $s.proc.HasExited) { Stop-Leftovers (@(Get-Tree $s.proc.Id) + $s.proc.Id) }; return }
    $eph = Join-Path $s.data 'profiles\ephemeral'
    Add-Check 'clean-shutdown' 'a Private profile exists while the session is live' (@(Get-ChildItem $eph -Directory -ErrorAction SilentlyContinue).Count -ge 1)
    $tree = @(Get-Tree $s.proc.Id)
    $null = $s.proc.CloseMainWindow()
    $exited = $s.proc.WaitForExit(40000)
    Add-Check 'clean-shutdown' 'the app closes by itself with a live Private session' $exited
    if (-not $exited) { Stop-Process -Id $s.proc.Id -Force }
    Start-Sleep 4
    $stragglers = @($tree | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue })
    Add-Check 'clean-shutdown' 'no app or engine process from the run survives' ($stragglers.Count -eq 0) "$($stragglers.Count) left"
    Stop-Leftovers $stragglers
    $left = @(Get-ChildItem $eph -Directory -ErrorAction SilentlyContinue)
    Add-Check 'clean-shutdown' 'the throwaway profile is deleted, not left for the next start' ($left.Count -eq 0) "$($left.Count) dir(s)"
    $hits = @(Bytes-Contain $s.data $markers)
    Add-Check 'clean-shutdown' 'nothing of the Private session is on disk outside its profile folder' ($hits.Count -eq 0) ($hits -join '; ')
    $r = Db-Report (Db-Copy $s.data (Join-Path $run 'dbcopy-clean'))
    if ($null -eq $r) { $script:inconclusive = $true } else {
        Add-Check 'clean-shutdown' 'integrity_check passes' ($r.integrity -eq 'ok')
        Add-Check 'clean-shutdown' 'the normal tab was saved; no Private tab row' (@($r.tabs | Where-Object { $_ -match 'normal-marker=keep-4242' }).Count -eq 1 -and @($r.tabs | Where-Object { $_ -match 'private-marker' }).Count -eq 0)
    }
}

function Test-CorruptDb {
    Write-Host '[corrupt-db] starting the app on a browser.db that is not a database'
    $data = Join-Path $run 'data-corrupt'; New-Item -ItemType Directory -Path "$data\db" -Force | Out-Null
    '{"firstRunDone":true}' | Set-Content "$data\settings.json" -Encoding ascii
    $garbage = [Text.Encoding]::ASCII.GetBytes(('this is not a sqlite database ' * 300))
    [IO.File]::WriteAllBytes("$data\db\browser.db", $garbage)
    $p = Start-App $data @()
    $win = Find-Window $p
    Start-Sleep -Seconds 15
    Add-Check 'corrupt-db' 'the app starts and stays up with a damaged database' ($win -and -not $p.HasExited)
    $kept = @(Get-ChildItem "$data\db" -Filter 'browser.db.corrupt-*' -File | Where-Object { $_.Name -notmatch '-(wal|shm)$' })
    Add-Check 'corrupt-db' 'the damaged file was set aside, not deleted' ($kept.Count -eq 1) "$($kept.Count) file(s)"
    $tree = @(Get-Tree $p.Id)
    # The person is told: the status line names what happened and where the old file is.
    $texts = @(); try { $texts = @($win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name }) } catch { }
    Add-Check 'corrupt-db' 'the person is told what happened and that the old file was kept' (@($texts | Where-Object { $_ -match 'could not be read' -and $_ -match 'kept, not deleted' }).Count -ge 1)
    # Acknowledge the notice the way a person would (the OK button), then close the window.
    try { $ok = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.AndCondition((New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'OK')), (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button))))); Add-Check 'corrupt-db' 'the notice has an OK button that dismisses it' ([bool]$ok); if ($ok) { $ok.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 1 } } catch { Add-Check 'corrupt-db' 'the notice has an OK button that dismisses it' $false $_.Exception.Message }
    $null = $p.CloseMainWindow(); if (-not $p.WaitForExit(40000)) { Stop-Process -Id $p.Id -Force; Add-Check 'corrupt-db' 'closes normally' $false } else { Add-Check 'corrupt-db' 'closes normally' $true }
    Start-Sleep 3; Stop-Leftovers @($tree | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue })
    $r = Db-Report (Db-Copy $data (Join-Path $run 'dbcopy-corrupt'))
    if ($null -eq $r) { $script:inconclusive = $true } else {
        Add-Check 'corrupt-db' 'the new database is healthy and holds the tab that was opened' ($r.integrity -eq 'ok' -and @($r.tabs).Count -ge 1) "user_version $($r.user_version), $(@($r.tabs).Count) tab(s)"
    }
}

if ($Scenario -in 'crash', 'all') { Test-Crash }
if ($Scenario -in 'clean-shutdown', 'all') { Test-CleanShutdown }
if ($Scenario -in 'corrupt-db', 'all') { Test-CorruptDb }

$failed = @($results.Values | ForEach-Object { $_.GetEnumerator() } | Where-Object { -not $_.Value.pass })
$total = @($results.Values | ForEach-Object { $_.GetEnumerator() }).Count
$verdict = if ($inconclusive) { 'INCONCLUSIVE' } elseif ($failed.Count) { 'FAIL' } else { 'PASS' }
$summary = [ordered]@{ verdict = $verdict; checks = $total; failed = $failed.Count; exe = $exe; at = (Get-Date).ToUniversalTime().ToString('o'); results = $results
    scope = 'Process-level: the app is killed with TerminateProcess or closed normally. Not covered: power loss or a failing disk (OS cache lost), a kill during the app''s own migration (covered by unit tests with a child process), Windows sign-out ordering.' }
$summary | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $run 'recovery-result.json') -Encoding utf8
Write-Host "`n$verdict ($total checks, $($failed.Count) failed). Details: $run"
if ($inconclusive) { exit 2 } elseif ($failed.Count) { exit 1 } else { exit 0 }
