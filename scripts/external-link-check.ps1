<#
.SYNOPSIS
  Links from other programs: a second start with a web address hands it to the running JevBrowse (one browser per data folder) and exits; an address that is not
  http/https is ignored; a cold start with an address opens it. Every wait has a timeout; a timeout is a FAIL or INCONCLUSIVE, never a PASS.
  Exit 0 = pass, 1 = fail, 2 = inconclusive.
#>
param(
    [string]$ExePath = '',
    [string]$Configuration = 'release',
    [string]$Root = 'D:\Browser\_ui-check\external'
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


$data = Join-Path $run 'data'; New-Item -ItemType Directory -Path "$data\benchmarks" -Force | Out-Null
'{"firstRunDone":true}' | Set-Content "$data\settings.json" -Encoding ascii
$a = Start-App $data
$wa = Window $a
if (-not $wa) { Check 'the first instance starts' $false; $inconclusive = $true }
else {
    Start-Sleep -Seconds 4
    $before = AddressText $wa
    $b = Start-App $data @('https://example.org/jev-external-link')
    $exited = $b.WaitForExit(30000)
    Check 'a second start with a link hands it over and exits' $exited
    if (-not $exited -and (Test-Same $b.Id)) { Stop-Process -Id $b.Id -Force }
    $addr = $null; for ($i = 0; $i -lt 30 -and $addr -notmatch 'jev-external-link'; $i++) { Start-Sleep -Milliseconds 500; $addr = AddressText $wa }
    Check 'the running JevBrowse opened the link in front' ($addr -match 'jev-external-link') "before: $before; now: $addr"
    Check 'there is still one JevBrowse window process for this data folder' (@(Get-Process JevBrowse.App -ErrorAction SilentlyContinue | Where-Object { Test-Same $_.Id }).Count -le 1)

    $c = Start-App $data @('javascript:alert(1)')
    $cx = $c.WaitForExit(30000)
    Start-Sleep -Seconds 2
    $addr2 = AddressText $wa
    Check 'an address that is not http or https is not opened' ($cx -and $addr2 -match 'jev-external-link') "now: $addr2"
    Check 'the first instance closes normally' (Close-App $a)

    # A copy that starts while some other process holds this data folder must refuse to open it (the lock does not depend on the app-instance service).
    $lockFile = Join-Path $data 'instance.lock'
    $held = $null
    try {
        $held = [IO.File]::Open($lockFile, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $e = Start-App $data
        $AE = [System.Windows.Automation.AutomationElement]
        $dlg = Window $e 20
        $texts = @(); $okBtn = $null
        if ($dlg) {
            $texts = @($dlg.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name })
            $okBtn = $dlg.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
        }
        Check 'the refused copy tells the person why (a message, not a silent exit)' (($texts -join ' ') -match 'already using this data folder') ($texts -join ' | ')
        if ($dlg) { try { $dlg.SetFocus() } catch { }; Add-Type -AssemblyName System.Windows.Forms; Start-Sleep -Milliseconds 500; [System.Windows.Forms.SendKeys]::SendWait('{ENTER}') }   # a native message box exposes no invokable button
        $ex = $e.WaitForExit(30000)
        Check 'a copy started while the data folder is held by another process refuses to open it' ($ex -and $e.ExitCode -eq 1) "exited=$ex code=$(if ($ex) { $e.ExitCode })"
        if (-not $ex -and (Test-Same $e.Id)) { Stop-Process -Id $e.Id -Force }
    } catch { Check 'a copy started while the data folder is held by another process refuses to open it' $false $_.Exception.Message }
    finally { if ($held) { $held.Dispose() } }
    Start-Sleep -Seconds 1

    $d = Start-App $data @('https://example.org/jev-cold-start')
    $wd = Window $d
    $addr3 = $null; for ($i = 0; $i -lt 60 -and $addr3 -notmatch 'jev-cold-start'; $i++) { Start-Sleep -Milliseconds 500; $addr3 = AddressText $wd }
    Check 'a cold start with a link opens it' ($addr3 -match 'jev-cold-start') "address: $addr3"
    $null = Close-App $d
}
$failed = @($results.GetEnumerator() | Where-Object { -not $_.Value.pass }).Count
$verdict = if ($failed) { 'FAIL' } elseif ($inconclusive) { 'INCONCLUSIVE' } else { 'PASS' }
[ordered]@{ verdict = $verdict; exe = $exe; at = (Get-Date).ToUniversalTime().ToString('o'); results = $results } | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $run 'external-link-check.json') -Encoding utf8
Write-Host "`n$verdict ($($results.Count) checks, $failed failed). $run"
if ($verdict -eq 'PASS') { exit 0 } elseif ($verdict -eq 'INCONCLUSIVE') { exit 2 } else { exit 1 }
