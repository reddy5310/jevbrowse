<#
.SYNOPSIS
  Proves the app honours the Windows "Animation effects" setting on the real setting, then puts the setting back.

.DESCRIPTION
  Reads the current value, runs the app once with animations ON and once with them OFF, and reads what the app reports
  (benchmarks/motion-report.json: its decision and how long a real fade took). The original value is restored in a
  finally block and read back; if it cannot be confirmed the script says so and exits non-zero.

  The change is made without SPIF_UPDATEINIFILE, so it is a session change, not a write to the user's saved profile,
  and Windows itself discards it at sign-out even if this script were killed. Only this one setting is touched.

  Exit: 0 both states behaved and the setting was restored; 1 a state misbehaved; 2 could not run a state (inconclusive);
  3 error, or the setting could not be confirmed restored.
#>
param([string]$Root = 'D:\Browser\_ui-check', [string]$Configuration = 'debug')
$ErrorActionPreference = 'Stop'
$script:exitCode = 3

Add-Type @'
using System; using System.Runtime.InteropServices;
public static class MotionSetting {
  [DllImport("user32.dll", SetLastError = true)] static extern bool SystemParametersInfo(uint action, uint param, ref int value, uint winIni);
  [DllImport("user32.dll", SetLastError = true)] static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint winIni);
  const uint GET = 0x1042, SET = 0x1043, SENDCHANGE = 0x2;
  public static bool Get() { int v = 0; if (!SystemParametersInfo(GET, 0, ref v, 0)) throw new System.ComponentModel.Win32Exception(); return v != 0; }
  public static void Set(bool on) { if (!SystemParametersInfo(SET, 0, (IntPtr)(on ? 1 : 0), SENDCHANGE)) throw new System.ComponentModel.Win32Exception(); }
}
'@
. (Join-Path $PSScriptRoot 'env.ps1') | Out-Null

$rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\')
if ($Root -notmatch '^[A-Za-z]:[\\/]' -or $rootFull.Length -lt 8) { [ordered]@{ verdict = 'ERROR'; error = "Refusing test root '$Root'" } | ConvertTo-Json; exit 3 }
$exe = Resolve-Path (Join-Path $PSScriptRoot "..\artifacts\bin\JevBrowse.App\${Configuration}_win-x64\JevBrowse.App.exe")

function Run-State([bool]$on) {
    $d = Join-Path $rootFull ('motion-{0}-{1}' -f ($(if ($on) { 'on' } else { 'off' })), [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory $d | Out-Null
    '{"firstRunDone":true}' | Set-Content "$d\settings.json" -Encoding ascii
    [MotionSetting]::Set($on)
    Start-Sleep -Milliseconds 800
    $seen = [MotionSetting]::Get()
    if ($seen -ne $on) { return [ordered]@{ state = $on; verdict = 'INCONCLUSIVE'; note = "asked Windows for $on but it reports $seen" } }
    $env:JEVBROWSE_DATA_DIR = $d; $env:JEVBROWSE_NO_FILTER_UPDATE = '1'; $env:JEVBROWSE_AI = '0'; $env:JEVBROWSE_MODE = 'Power'
    $env:JEVBROWSE_START_URL = 'jev://welcome/'; Remove-Item Env:\JEVBROWSE_MOTION -ErrorAction SilentlyContinue
    $p = Start-Process $exe -ArgumentList '--ui-shot' -PassThru -Wait -WindowStyle Minimized
    $f = "$d\benchmarks\motion-report.json"
    if (-not (Test-Path $f)) { return [ordered]@{ state = $on; verdict = 'INCONCLUSIVE'; note = 'the app wrote no motion report' } }
    $r = Get-Content $f -Raw | ConvertFrom-Json
    $ok = if ($on) { $r.animationsEnabled -eq $true -and $r.fadeCompleted -and $r.fadeMilliseconds -ge 150 -and $r.policyBaseMilliseconds -gt 0 }
          else { $r.animationsEnabled -eq $false -and $r.fadeCompleted -and $r.fadeMilliseconds -lt 60 -and $r.policyBaseMilliseconds -eq 0 }
    [ordered]@{ state = $(if ($on) { 'animations ON' } else { 'animations OFF' }); verdict = $(if ($ok) { 'PASS' } else { 'FAIL' }); report = $r }
}

$original = [MotionSetting]::Get()
$results = @(); $restored = $false; $err = $null
try {
    New-Item -ItemType Directory -Force $rootFull | Out-Null
    foreach ($state in @($true, $false)) { $results += , (Run-State $state) }
}
catch { $err = $_.Exception.Message }
finally {
    try { [MotionSetting]::Set($original); Start-Sleep -Milliseconds 500; $restored = ([MotionSetting]::Get() -eq $original) } catch { $restored = $false }
}
$verdicts = @($results | ForEach-Object { $_.verdict })
$verdict = if ($err -or -not $restored) { 'ERROR' } elseif ($verdicts -contains 'FAIL') { 'FAIL' } elseif ($verdicts -contains 'INCONCLUSIVE' -or $verdicts.Count -lt 2) { 'INCONCLUSIVE' } else { 'PASS' }
$script:exitCode = switch ($verdict) { 'PASS' { 0 } 'FAIL' { 1 } 'INCONCLUSIVE' { 2 } default { 3 } }
[ordered]@{ verdict = $verdict; pass = ($verdict -eq 'PASS'); originalSetting = $(if ($original) { 'animations ON' } else { 'animations OFF' }); settingRestoredAndConfirmed = $restored; error = $err; states = $results } | ConvertTo-Json -Depth 5
exit $script:exitCode
