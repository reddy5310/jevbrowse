<#
.SYNOPSIS
  Short targeted checks for the private-alpha additions that need a real restart or a real missing dependency:
    resume     the last ordinary workspace and active tab come back after a normal close and restart, lazily; a Private session in front when the app was
               closed is NOT resumed and nothing of it is written to the preferences file.
    runtime    a WebView2 Runtime that cannot be found produces a useful message with a way to get it, and Quit closes the app.
  The engine-level checks (permission reset, clearing one profile's data, Private downloads, popup sign-in) are in the app: run it with --additions-check.
  Every wait has a timeout; a timeout is a FAIL or INCONCLUSIVE, never a PASS. Processes are identified by id AND start time before anything is stopped.
  Exit 0 = pass, 1 = fail, 2 = inconclusive.
#>
param(
    [string]$ExePath = '',
    [string]$Configuration = 'debug',
    [string]$Root = 'D:\Browser\_ui-check\additions'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$exe = if ($ExePath) { $ExePath } else { Join-Path $PSScriptRoot "..\artifacts\bin\JevBrowse.App\${Configuration}_win-x64\JevBrowse.App.exe" }
$exe = (Resolve-Path $exe).Path
$run = Join-Path ([IO.Path]::GetFullPath($Root)) ("{0}-{1}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), [guid]::NewGuid().ToString('N').Substring(0, 6))
New-Item -ItemType Directory -Path $run -Force | Out-Null
$results = [ordered]@{}; $inconclusive = $false
function Check($name, $ok, $detail = '') { $results[$name] = [ordered]@{ pass = [bool]$ok; detail = $detail }; Write-Host ("  {0,-4} {1} {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail) }

$script:Born = @{}
function Note($p) { try { $script:Born[[int]$p.Id] = $p.StartTime } catch { } }
function Test-Same($id) { $p = Get-Process -Id $id -ErrorAction SilentlyContinue; if (-not $p) { return $false }; try { $script:Born.ContainsKey([int]$id) -and [Math]::Abs(($p.StartTime - $script:Born[[int]$id]).TotalSeconds) -lt 2 } catch { $false } }
function Tree([int]$root) { $all = @(Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId, CreationDate); $ids = New-Object System.Collections.Generic.List[int]; $q = New-Object System.Collections.Generic.Queue[int]; $q.Enqueue($root); while ($q.Count) { $x = $q.Dequeue(); foreach ($c in ($all | Where-Object { $_.ParentProcessId -eq $x })) { $ids.Add([int]$c.ProcessId); $script:Born[[int]$c.ProcessId] = [datetime]$c.CreationDate; $q.Enqueue([int]$c.ProcessId) } }; $ids }
function Start-App($data, [string[]]$argv = @(), $env2 = @{}) {
    foreach ($n in (Get-ChildItem Env: | Where-Object { $_.Name -like 'JEVBROWSE_*' -or $_.Name -like 'WEBVIEW2_*' }).Name) { Remove-Item "Env:\$n" }
    $env:JEVBROWSE_DATA_DIR = $data; $env:JEVBROWSE_NO_FILTER_UPDATE = '1'; $env:JEVBROWSE_AI = '0'; $env:JEVBROWSE_DEVSPACE = '0'
    foreach ($k in $env2.Keys) { Set-Item "Env:\$k" $env2[$k] }
    $p = if ($argv.Count) { Start-Process $exe -ArgumentList $argv -PassThru } else { Start-Process $exe -PassThru }
    Note $p; $p
}
function Window($p, $seconds = 40) { $AE = [System.Windows.Automation.AutomationElement]; for ($i = 0; $i -lt $seconds * 2 -and -not $p.HasExited; $i++) { $w = $AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id))); if ($w) { return $w }; Start-Sleep -Milliseconds 500 } }
function Close-App($p) { $tree = @(Tree $p.Id); $null = $p.CloseMainWindow(); $ok = $p.WaitForExit(40000); if (-not $ok -and (Test-Same $p.Id)) { Stop-Process -Id $p.Id -Force }; Start-Sleep 3; foreach ($id in $tree) { if (Test-Same $id) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue } }; $ok }
function AddressText($w) { try { $a = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'AddressBox'))); $a.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { $null } }

# ------------------------------------------------------------------ resume
Write-Host '[resume]'
$data = Join-Path $run 'resume'; New-Item -ItemType Directory -Path "$data\benchmarks" -Force | Out-Null
'{"firstRunDone":true}' | Set-Content "$data\settings.json" -Encoding ascii
$p1 = Start-App $data @('--resume-seed')
$w1 = Window $p1
$seed = "$data\benchmarks\resume-seed.json"
for ($i = 0; $i -lt 90 -and -not (Test-Path $seed) -and -not $p1.HasExited; $i++) { Start-Sleep -Milliseconds 500 }
if (-not (Test-Path $seed)) { Check 'the seeded session was set up' $false 'the app never reached the seeded state'; $inconclusive = $true; if (Test-Same $p1.Id) { Stop-Process -Id $p1.Id -Force } }
else {
    $s = Get-Content $seed -Raw | ConvertFrom-Json
    Check 'the seeded session was set up (a Private tab was in front at close)' ($s.privateInFront -eq $true)
    Check 'the app closes normally' (Close-App $p1)
    $prefs = Get-Content "$data\ui-prefs.json" -Raw
    Check 'the preferences file remembers the ordinary tab and workspace' ($prefs -match $s.second -and $prefs -match $s.work)
    Check 'nothing of the Private session or any address is in the preferences file' ($prefs -notmatch 'PRIVATE' -and $prefs -notmatch 'http' -and $prefs -notmatch 'jev:')
    $p2 = Start-App $data
    $w2 = Window $p2
    if (-not $w2) { Check 'the app starts again' $false; $inconclusive = $true }
    else {
        $addr = $null; for ($i = 0; $i -lt 40 -and $addr -notmatch 'resume='; $i++) { Start-Sleep -Milliseconds 500; $addr = AddressText $w2 }
        Check 'the last ordinary tab is back in front' ($addr -match 'resume=second') "address bar: $addr"
        Check 'the Private tab was not resumed' ($addr -notmatch 'PRIVATE')
        $names = @($w2.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name })
        Check 'it is restored lazily: only that tab is awake' (@($names | Where-Object { $_ -match '^\d+ tabs open, 1 awake' }).Count -ge 1) (($names | Where-Object { $_ -match 'tabs open' } | Select-Object -First 1))
        # The menu entries exist, and the clear-data dialog says what it does and defaults to Cancel.
        try {
            $AE = [System.Windows.Automation.AutomationElement]
            function Find-Named($root, $name, $type = $null) { $c = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name); if ($type) { $c = New-Object System.Windows.Automation.AndCondition($c, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $type))) }; $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c) }
            $menu = Find-Named $w2 'This tab menu' ([System.Windows.Automation.ControlType]::Button)
            $menu.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Seconds 1
            $item = Find-Named $AE::RootElement "Clear website data$([char]0x2026)"
            Check 'the tab menu offers Site permissions and Clear website data' ($item -and (Find-Named $AE::RootElement "Site permissions$([char]0x2026)"))
            $item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Seconds 2
            $n2 = @($w2.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name })
            $t = ($n2 -join ' | ')
            Check 'the clear-data dialog is scoped to the profile, warns about sign-out and says what is not deleted' ($t -match 'Clear website data for the Work profile' -and $t -match 'signed out' -and $t -match 'downloaded' -and $t -match 'Browser Memory')
            $cancel = Find-Named $w2 'Cancel' ([System.Windows.Automation.ControlType]::Button)
            if ($cancel) { $cancel.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 1 }
            Check 'Cancel leaves everything as it was (dialog gone)' ($cancel -and -not (Find-Named $w2 'Clear Work profile' ([System.Windows.Automation.ControlType]::Button)))
        } catch { Check 'the tab menu and clear-data dialog can be driven' $false $_.Exception.Message }
        Check 'the app closes normally again' (Close-App $p2)
    }
}

# ------------------------------------------------------------------ missing runtime
Write-Host '[missing WebView2 runtime (simulated: the runtime folder points at an empty directory)]'
$empty = Join-Path $run 'no-runtime'; New-Item -ItemType Directory -Path $empty -Force | Out-Null
$data3 = Join-Path $run 'noruntime-data'; New-Item -ItemType Directory -Path $data3 -Force | Out-Null
$p3 = Start-App $data3 @() @{ WEBVIEW2_BROWSER_EXECUTABLE_FOLDER = $empty }
$w3 = Window $p3 30
if (-not $w3) { Check 'the app shows a window even without a runtime' $false "exited=$($p3.HasExited)"; $inconclusive = $true }
else {
    $found = $false
    for ($i = 0; $i -lt 30 -and -not $found; $i++) {
        Start-Sleep -Milliseconds 500
        $n = @($w3.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name })
        $found = @($n | Where-Object { $_ -match 'WebView2 Runtime' }).Count -ge 1
    }
    Check 'a clear message names the missing WebView2 Runtime' $found
    Check 'it offers to get the runtime' (@($n | Where-Object { $_ -eq 'Get the WebView2 Runtime' }).Count -ge 1)
    try {
        $q = $w3.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.AndCondition((New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Quit')), (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))))
        $q.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Check 'Quit closes the app' ($p3.WaitForExit(20000))
    } catch { Check 'Quit closes the app' $false $_.Exception.Message }
    if (Test-Same $p3.Id) { Stop-Process -Id $p3.Id -Force }
}

$failed = @($results.GetEnumerator() | Where-Object { -not $_.Value.pass }).Count
$verdict = if ($failed) { 'FAIL' } elseif ($inconclusive) { 'INCONCLUSIVE' } else { 'PASS' }
[ordered]@{ verdict = $verdict; exe = $exe; at = (Get-Date).ToUniversalTime().ToString('o'); results = $results } | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $run 'additions-check.json') -Encoding utf8
Write-Host "`n$verdict ($($results.Count) checks, $failed failed). $run"
if ($verdict -eq 'PASS') { exit 0 } elseif ($verdict -eq 'INCONCLUSIVE') { exit 2 } else { exit 1 }
