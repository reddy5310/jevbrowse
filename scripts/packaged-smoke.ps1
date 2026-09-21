<#
.SYNOPSIS
  Smoke test of the PACKAGED alpha: the release ZIP, verified against its checksum, extracted to a fresh folder, and launched the way a person would.
  It does not use a development build, an artifacts folder or any JEVBROWSE_* setting except the DATA folder (so nothing is written to C:; pass -DefaultData on
  a clean Windows account or VM to test the real default location, %LOCALAPPDATA%\JevBrowse).

  Checks: checksum matches; the ZIP contains the app and its BUILD.json (commit, version); first launch shows a window with the welcome page and the
  first-run tips; the window closes by itself; a second launch has no first-run tips and the saved tab list is intact; an "offline" launch (WebView2 told to use a
  dead proxy) still starts and stays up; then scripts\recovery-check.ps1 is run against the extracted app (crash, clean shutdown, damaged database, agent
  close, newer database).

  NOT covered here, and needing a clean VM or account: missing WebView2 runtime, real network loss, install over an older version, downloads and uploads,
  sign-in persistence with a real account. Those are listed in docs/FIRST_RELEASE_PLAN.md.

  Exit 0 = all passed, 1 = a check failed, 2 = inconclusive (the app never showed a window, or a prerequisite is missing).
#>
param(
    [Parameter(Mandatory)][string]$Zip,
    [string]$Root = 'D:\Browser\_ui-check\packaged',
    [switch]$DefaultData,
    [switch]$SkipRecovery
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.IO.Compression.FileSystem
$Zip = (Resolve-Path $Zip).Path
$rootFull = [IO.Path]::GetFullPath($Root)
$run = Join-Path $rootFull ("{0}-{1}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), [guid]::NewGuid().ToString('N').Substring(0, 6))
New-Item -ItemType Directory -Path $run -Force | Out-Null
$results = [ordered]@{}; $inconclusive = $false
function Check($name, $ok, $detail = '') { $results[$name] = [ordered]@{ pass = [bool]$ok; detail = $detail }; Write-Host ("  {0,-4} {1} {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail) }

# ---- the artifact itself
$sums = Join-Path (Split-Path $Zip) 'SHA256SUMS.txt'
if (Test-Path $sums) {
    $line = Get-Content $sums | Where-Object { $_ -match [regex]::Escape((Split-Path $Zip -Leaf)) } | Select-Object -First 1
    $want = ($line -split '\s+')[0]
    $got = (Get-FileHash $Zip -Algorithm SHA256).Hash.ToLower()
    Check 'zip matches its published SHA-256' ($want -eq $got) $got
} else { Check 'zip matches its published SHA-256' $false 'no SHA256SUMS.txt next to the zip'; $inconclusive = $true }
$app = Join-Path $run 'app'
[IO.Compression.ZipFile]::ExtractToDirectory($Zip, $app)
$exe = Join-Path $app 'JevBrowse.App.exe'
Check 'the zip contains the app' (Test-Path $exe)
if (-not (Test-Path $exe)) { exit 2 }
$build = if (Test-Path "$app\BUILD.json") { Get-Content "$app\BUILD.json" -Raw | ConvertFrom-Json } else { $null }
Check 'BUILD.json records the commit and version' ($null -ne $build -and $build.commit -match '^[0-9a-f]{40}$') "$($build.version) $($build.commit)"
$unsafe = @(Get-ChildItem $app -Recurse -File | Where-Object { $_.Name -match '\.(pdb)$|^\.env$|appsettings\.Development' })
Check 'no development leftovers (.env, dev settings, debug symbols) in the package' ($unsafe.Count -eq 0) (($unsafe | Select-Object -First 3 | ForEach-Object Name) -join ', ')

# ---- launching, the way a person does
$data = Join-Path $run 'userdata'
function Launch([hashtable]$extraEnv = @{}) {
    foreach ($n in (Get-ChildItem Env: | Where-Object { $_.Name -like 'JEVBROWSE_*' }).Name) { Remove-Item "Env:\$n" }
    Remove-Item Env:\WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS -ErrorAction SilentlyContinue
    if (-not $DefaultData) { $env:JEVBROWSE_DATA_DIR = $data }
    foreach ($k in $extraEnv.Keys) { Set-Item "Env:\$k" $extraEnv[$k] }
    Start-Process $exe -PassThru
}
function Window($p, $seconds = 40) {
    $AE = [System.Windows.Automation.AutomationElement]
    for ($i = 0; $i -lt $seconds * 2 -and -not $p.HasExited; $i++) {
        $w = $AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id)))
        if ($w) { return $w }; Start-Sleep -Milliseconds 500
    }
}
function Names($w) { try { @($w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name } | Where-Object { $_ }) } catch { @() } }
$script:Born = @{}
function Test-Same($id) { $p = Get-Process -Id $id -ErrorAction SilentlyContinue; if (-not $p) { return $false }; try { return ($script:Born.ContainsKey([int]$id) -and [Math]::Abs(($p.StartTime - $script:Born[[int]$id]).TotalSeconds) -lt 2) } catch { return $false } }   # ids are reused by Windows: same id AND same start time
function Tree([int]$root) { $all = @(Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId, CreationDate); $me = $all | Where-Object { $_.ProcessId -eq $root }; if ($me) { $script:Born[[int]$root] = [datetime]$me.CreationDate }; $ids = New-Object System.Collections.Generic.List[int]; $q = New-Object System.Collections.Generic.Queue[int]; $q.Enqueue($root); while ($q.Count) { $x = $q.Dequeue(); foreach ($c in ($all | Where-Object { $_.ParentProcessId -eq $x })) { $ids.Add([int]$c.ProcessId); $script:Born[[int]$c.ProcessId] = [datetime]$c.CreationDate; $q.Enqueue([int]$c.ProcessId) } }; $ids }
function Close-App($p, $tips = $false) {
    $tree = @(Tree $p.Id)
    # Go through the first-run tips the way a person does: press "Next" until there are no more (the app remembers first-run only after the last one).
    if ($tips) {
        $AE = [System.Windows.Automation.AutomationElement]
        for ($i = 0; $i -lt 8; $i++) {
            try {
                $w = $AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id)))
                $b = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.AndCondition((New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, 'Next')), (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))))
                if ($b) { $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 1200 } else { break }
            } catch { break }
        }
        Start-Sleep 1
    }
    $null = $p.CloseMainWindow()
    $ok = $p.WaitForExit(40000)
    if (-not $ok) { Stop-Process -Id $p.Id -Force }
    Start-Sleep 3
    foreach ($id in $tree) { if (Test-Same $id) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue } }
    $ok
}

Write-Host '[first launch]'
$p1 = Launch
$w1 = Window $p1
if (-not $w1) { Check 'first launch shows a window' $false; $inconclusive = $true }
else {
    Check 'first launch shows a window' $true
    Start-Sleep 10
    $names = Names $w1
    Check 'the welcome page is showing' (@($names | Where-Object { $_ -match 'welcome' -or $_ -match 'Welcome' }).Count -ge 1) (($names | Where-Object { $_ -match 'welcome|Welcome' } | Select-Object -First 1))
    Check 'the first-run tips appear on a first launch' (@($names | Where-Object { $_ -match 'Your tabs live here' }).Count -ge 1)
    Check 'the window closes by itself' (Close-App $p1 $true)
}
$settings = Get-ChildItem $data -Filter settings.json -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $DefaultData) { Check 'first-run is remembered after the tips' ($settings -and (Get-Content $settings.FullName -Raw) -match 'firstRunDone') }

Write-Host '[second launch]'
$p2 = Launch
$w2 = Window $p2
if (-not $w2) { Check 'second launch shows a window' $false; $inconclusive = $true }
else {
    Check 'second launch shows a window' $true
    Start-Sleep 8
    $n2 = Names $w2
    Check 'no first-run tips the second time' (@($n2 | Where-Object { $_ -match 'Your tabs live here' }).Count -eq 0)
    Check 'the saved tab is back' (@($n2 | Where-Object { $_ -match '\. open ' }).Count -ge 1 -and @($n2 | Where-Object { $_ -match '^Close ' }).Count -ge 1) (($n2 | Where-Object { $_ -match '\. open ' } | Select-Object -First 1))
    Check 'the window closes by itself' (Close-App $p2)
}

Write-Host '[offline launch (WebView2 pointed at a dead proxy)]'
$p3 = Launch @{ WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = '--proxy-server=127.0.0.1:9' }
$w3 = Window $p3
Check 'a launch with no reachable network shows a window' ([bool]$w3)
if ($w3) { Start-Sleep 10; Check 'it stays up (does not hang or crash)' (-not $p3.HasExited); Check 'the window closes by itself' (Close-App $p3) }

# ---- recovery checks against THIS build
if (-not $SkipRecovery) {
    Write-Host '[recovery checks against the extracted app]'
    foreach ($n in (Get-ChildItem Env: | Where-Object { $_.Name -like 'JEVBROWSE_*' }).Name) { Remove-Item "Env:\$n" }
    Remove-Item Env:\WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS -ErrorAction SilentlyContinue
    & powershell -NoProfile -File (Join-Path $PSScriptRoot 'recovery-check.ps1') -Scenario all -ExePath $exe -Root (Join-Path $run 'recovery') | Select-Object -Last 3 | ForEach-Object { Write-Host "  $_" }
    Check 'recovery checks passed on the packaged build' ($LASTEXITCODE -eq 0) "exit $LASTEXITCODE"
    if ($LASTEXITCODE -eq 2) { $inconclusive = $true }
}

# ---- the private-alpha additions against THIS build (each wait has a timeout; a timeout is a failure, never a pass)
Write-Host '[additions: restart resume, missing WebView2 runtime, permissions, clear data, Private download prompt]'
foreach ($n in (Get-ChildItem Env: | Where-Object { $_.Name -like 'JEVBROWSE_*' -or $_.Name -like 'WEBVIEW2_*' }).Name) { Remove-Item "Env:\$n" }
& powershell -NoProfile -File (Join-Path $PSScriptRoot 'additions-check.ps1') -ExePath $exe -Root (Join-Path $run 'additions') | Select-Object -Last 2 | ForEach-Object { Write-Host "  $_" }
Check 'restart resume and missing-runtime checks passed on the packaged build' ($LASTEXITCODE -eq 0) "exit $LASTEXITCODE"
if ($LASTEXITCODE -eq 2) { $script:inconclusive = $true }
$ad = Join-Path $run 'additions-engine'; New-Item -ItemType Directory -Path "$ad\benchmarks" -Force | Out-Null
'{"firstRunDone":true}' | Set-Content "$ad\settings.json" -Encoding ascii
$env:JEVBROWSE_DATA_DIR = $ad; $env:JEVBROWSE_NO_FILTER_UPDATE = '1'; $env:JEVBROWSE_AI = '0'; $env:JEVBROWSE_MODE = 'Simple'
$pa = Start-Process $exe -ArgumentList '--additions-check' -PassThru
$done = $pa.WaitForExit(150000)
if (-not $done) { Stop-Process -Id $pa.Id -Force }   # $pa is a handle we hold, so its id cannot have been reused
$resFile = "$ad\benchmarks\additions-check.json"
if ($done -and (Test-Path $resFile)) {
    $r = Get-Content $resFile -Raw | ConvertFrom-Json
    Check 'permission reset, per-profile data clearing, Private-download prompt and the recorded popup-login behaviour (engine checks)' ($r.pass -eq $true) "$(@($r.steps).Count) steps; failed: $((@($r.steps | Where-Object { -not $_.ok }) | ForEach-Object { $_.name }) -join '; ')"
} else { Check 'permission reset, per-profile data clearing, Private-download prompt and the recorded popup-login behaviour (engine checks)' $false 'timed out or produced no result (a failure, not a pass)' }
Remove-Item Env:\JEVBROWSE_DATA_DIR, Env:\JEVBROWSE_NO_FILTER_UPDATE, Env:\JEVBROWSE_AI, Env:\JEVBROWSE_MODE -ErrorAction SilentlyContinue

# ---- unfinished-work protection against THIS build (password-only typing, iframe typing, upload in flight, silent video; bounded wait)
Write-Host '[unfinished-work protection]'
$pd = Join-Path $run 'protection'; New-Item -ItemType Directory -Path "$pd\benchmarks" -Force | Out-Null
'{"firstRunDone":true}' | Set-Content "$pd\settings.json" -Encoding ascii
$env:JEVBROWSE_DATA_DIR = $pd; $env:JEVBROWSE_NO_FILTER_UPDATE = '1'; $env:JEVBROWSE_AI = '0'; $env:JEVBROWSE_MODE = 'Simple'
$pp = Start-Process $exe -ArgumentList '--protection-check' -PassThru
$pdone = $pp.WaitForExit(180000)
if (-not $pdone) { Stop-Process -Id $pp.Id -Force }   # a handle we hold: its id cannot have been reused
$pres = "$pd\benchmarks\protection-check.json"
if ($pdone -and (Test-Path $pres)) { $pr = Get-Content $pres -Raw | ConvertFrom-Json; Check 'unfinished work keeps a tab awake (password-only typing, iframe typing, upload, silent video)' ($pr.pass -eq $true) "$(@($pr.steps).Count) steps; failed: $((@($pr.steps | Where-Object { -not $_.ok }) | ForEach-Object { $_.name }) -join '; ')" }
else { Check 'unfinished work keeps a tab awake (password-only typing, iframe typing, upload, silent video)' $false 'timed out or produced no result (a failure, not a pass)' }
Remove-Item Env:\JEVBROWSE_DATA_DIR, Env:\JEVBROWSE_NO_FILTER_UPDATE, Env:\JEVBROWSE_AI, Env:\JEVBROWSE_MODE -ErrorAction SilentlyContinue

$failed = @($results.GetEnumerator() | Where-Object { -not $_.Value.pass }).Count
$verdict = if ($inconclusive -and $failed -eq 0) { 'INCONCLUSIVE' } elseif ($failed) { 'FAIL' } else { 'PASS' }
[ordered]@{ verdict = $verdict; zip = $Zip; sha256 = (Get-FileHash $Zip -Algorithm SHA256).Hash.ToLower(); commit = $build.commit; version = $build.version; defaultDataLocation = [bool]$DefaultData
    at = (Get-Date).ToUniversalTime().ToString('o'); machine = "$([Environment]::OSVersion.VersionString), $([Environment]::ProcessorCount) logical cores"; results = $results } |
    ConvertTo-Json -Depth 5 | Set-Content (Join-Path $run 'packaged-smoke.json') -Encoding utf8
Write-Host "`n$verdict ($($results.Count) checks, $failed failed). $run"
if ($verdict -eq 'PASS') { exit 0 } elseif ($verdict -eq 'INCONCLUSIVE') { exit 2 } else { exit 1 }
