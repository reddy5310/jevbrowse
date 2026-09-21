<#
.SYNOPSIS
  Checks the app with Windows "Transparency effects" on and off, measured from real screen pixels, then puts the setting back.

.DESCRIPTION
  Personalization > Colors > Transparency effects (HKCU\...\Themes\Personalize\EnableTransparency). The app draws Mica behind an
  opaque-enough layout and claims that nothing depends on transparency; this measures it. For each state and each theme (dark, light):
    - the setting is applied and the APP is asked what it sees (UISettings.AdvancedEffectsEnabled); a state the app did not see is
      INCONCLUSIVE, not "fine";
    - the app is launched, brought to the front and its window is captured from the screen; the contrast ratio of the title-bar
      text against what is actually behind it is computed from those pixels (WCAG 4.5:1 for text this size);
    - the accessibility/layout gate runs once per state.
  SAFETY: the original value (or its absence) is recorded to <Root>\transparency-original.json BEFORE any change and restored in a
  finally block and read back. -Restore repairs a run that was killed. Only that one registry value is touched.
  A screen capture is only taken when the app is verifiably the foreground window, so it never photographs another program.

  Exit: 0 clean; 1 a defect; 2 inconclusive; 3 error or the original could not be confirmed restored.
#>
param(
    [string]$Root = 'D:\Browser\_ui-check',
    [switch]$Restore,
    [string]$Configuration = 'debug'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ProcessScope.ps1')
$script:exitCode = 3
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class TransWin {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
  [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
  public static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, string l, uint flags, uint timeout, out IntPtr result);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int H, bool r);
}
'@
[TransWin]::SetProcessDPIAware() | Out-Null

$rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\')
if ($Root -notmatch '^[A-Za-z]:[\\/]' -or $rootFull.Length -lt 8) { [ordered]@{ verdict = 'ERROR'; error = "Refusing test root '$Root'" } | ConvertTo-Json; exit 3 }
New-Item -ItemType Directory -Force $rootFull | Out-Null
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'
$name = 'EnableTransparency'
$marker = Join-Path $rootFull 'transparency-original.json'

function Read-Setting { $p = Get-ItemProperty -Path $key -Name $name -ErrorAction SilentlyContinue; if ($null -eq $p) { $null } else { [int]$p.$name } }
function Broadcast { $r = [IntPtr]::Zero; [TransWin]::SendMessageTimeout([IntPtr]0xFFFF, 0x1A, [IntPtr]::Zero, 'ImmersiveColorSet', 2, 2000, [ref]$r) | Out-Null }
function Write-Setting($value) {
    if ($null -eq $value) { Remove-ItemProperty -Path $key -Name $name -ErrorAction SilentlyContinue }
    else { Set-ItemProperty -Path $key -Name $name -Value ([int]$value) -Type DWord }
    Broadcast; Start-Sleep -Milliseconds 1200
}

if ($Restore) {
    if (-not (Test-Path $marker)) { [ordered]@{ verdict = 'ERROR'; error = 'no transparency-original.json: nothing to restore' } | ConvertTo-Json; exit 3 }
    $o = Get-Content $marker -Raw | ConvertFrom-Json
    Write-Setting $(if ($o.existed) { [int]$o.value } else { $null })
    $now = Read-Setting
    $ok = if ($o.existed) { $now -eq [int]$o.value } else { $null -eq $now }
    if ($ok) { ([ordered]@{ state = 'restored'; existed = $o.existed; value = $o.value; recordedAt = $o.recordedAt; restoredAt = (Get-Date).ToString('s') }) | ConvertTo-Json | Set-Content $marker -Encoding utf8 }
    [ordered]@{ verdict = $(if ($ok) { 'RESTORED' } else { 'ERROR' }); original = $o; nowInRegistry = $now } | ConvertTo-Json
    exit $(if ($ok) { 0 } else { 3 })
}

# Resolve the build first: a missing build must stop the run BEFORE any record is written or any setting is touched.
$exe = Resolve-Path (Join-Path $PSScriptRoot "..\artifacts\bin\JevBrowse.App\${Configuration}_win-x64\JevBrowse.App.exe")
$gate = Join-Path $PSScriptRoot 'ui-a11y-check.ps1'

# An earlier run that never confirmed its restore leaves its record marked active. Its "original" is the truth; the value in the
# registry now may be that run's temporary one. So a new run refuses to start, and never overwrites the record.
if (Test-Path $marker) {
    $prev = Get-Content $marker -Raw | ConvertFrom-Json
    if ($prev.state -eq 'active') {
        [ordered]@{ verdict = 'ERROR'; error = "An earlier run did not finish restoring Windows Transparency effects (recorded at $($prev.recordedAt); original: $(if ($prev.existed) { $prev.value } else { 'not set' })). Run this script with -Restore first." } | ConvertTo-Json
        exit 3
    }
}
$original = Read-Setting
[ordered]@{ state = 'active'; existed = ($null -ne $original); value = $original; recordedAt = (Get-Date).ToString('s') } | ConvertTo-Json | Set-Content $marker -Encoding utf8


# The visible frame of a window (DWMWA_EXTENDED_FRAME_BOUNDS). UI Automation's rectangle also covers the invisible resize borders, so a
# capture of it includes a strip of whatever is behind the window; a capture must be of the window and nothing else.
function Get-FrameRect([IntPtr]$h, $fallback) {
    $fr = New-Object TransWin+RECT
    if ([TransWin]::DwmGetWindowAttribute($h, 9, [ref]$fr, 16) -eq 0 -and ($fr.R - $fr.L) -gt 0) { return [pscustomobject]@{ X = $fr.L; Y = $fr.T; W = ($fr.R - $fr.L); H = ($fr.B - $fr.T) } }
    [pscustomobject]@{ X = [int]$fallback.X; Y = [int]$fallback.Y; W = [int]$fallback.Width; H = [int]$fallback.Height }
}

# A gate run counts as a pass only if it says so: exit 0 AND a report that parses, says PASS, and says its keyboard and panel checks
# COMPLETED. Exit 0 with no or unreadable output (a run that died quietly) is an ERROR, never a pass.
function Judge-Gate($code, $j) {
    if ($code -eq 1) { return 'FAIL' }
    if ($code -eq 2) { return 'INCONCLUSIVE' }
    if ($code -eq 0 -and $null -ne $j -and $j.verdict -eq 'PASS' -and $j.pass -eq $true -and $j.panelCheck -eq 'complete' -and $j.tabTraversal -eq 'complete') { return 'PASS' }
    return 'ERROR'
}
function Merge-Verdict([string]$current, [string]$new) {
    $rank = @{ PASS = 0; INCONCLUSIVE = 1; FAIL = 2; ERROR = 3 }
    if ($rank[$new] -gt $rank[$current]) { $new } else { $current }
}

function Get-Tree([int]$rootPid) {
    $all = @(Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId); $found = New-Object System.Collections.Generic.List[int]
    $q = New-Object System.Collections.Generic.Queue[int]; $q.Enqueue($rootPid)
    while ($q.Count) { $p = $q.Dequeue(); foreach ($c in ($all | Where-Object { $_.ParentProcessId -eq $p })) { if (-not $found.Contains([int]$c.ProcessId)) { $found.Add([int]$c.ProcessId); $q.Enqueue([int]$c.ProcessId) } } }
    $found
}
function Stop-Ours($proc) { Stop-OwnedProcesses (Get-OwnedProcesses $proc) }   # id AND start time (scripts\ProcessScope.ps1)
function New-Data([string]$tag, [string]$theme) {
    $d = Join-Path $rootFull ('tr-{0}-{1}' -f $tag, [guid]::NewGuid().ToString('N').Substring(0, 8)); New-Item -ItemType Directory $d | Out-Null
    '{"firstRunDone":true}' | Set-Content "$d\settings.json" -Encoding ascii
    $env:JEVBROWSE_DATA_DIR = $d; $env:JEVBROWSE_NO_FILTER_UPDATE = '1'; $env:JEVBROWSE_AI = '0'; $env:JEVBROWSE_MODE = 'Power'; $env:JEVBROWSE_THEME = $theme; $env:JEVBROWSE_START_URL = 'jev://welcome/'
    $d
}
function Rel-Lum([System.Drawing.Color]$c) {
    $f = { param($v) $v = $v / 255.0; if ($v -le 0.03928) { $v / 12.92 } else { [math]::Pow(($v + 0.055) / 1.055, 2.4) } }
    0.2126 * (& $f $c.R) + 0.7152 * (& $f $c.G) + 0.0722 * (& $f $c.B)
}

# Contrast of the title-bar text against what is really behind it, from captured pixels.
function Measure-Title([string]$png) {
    $bmp = [System.Drawing.Bitmap]::FromFile($png)
    try {
        $lums = New-Object System.Collections.Generic.List[double]
        for ($y = 10; $y -le 34; $y++) { for ($x = 40; $x -le 126; $x++) { $lums.Add((Rel-Lum $bmp.GetPixel($x, $y))) } }
        $sorted = $lums | Sort-Object
        $bg = $sorted[[int]($sorted.Count / 2)]                                   # the median is the background, text being a minority of pixels
        $far = $lums | Sort-Object { [math]::Abs($_ - $bg) } -Descending | Select-Object -First ([int]($lums.Count * 0.03))
        $fg = ($far | Measure-Object -Average).Average
        $hi = [math]::Max($fg, $bg); $lo = [math]::Min($fg, $bg)
        [math]::Round((($hi + 0.05) / ($lo + 0.05)), 2)
    }
    finally { $bmp.Dispose() }
}

function Run-Capture([string]$tag, [string]$theme) {
    $d = New-Data $tag $theme
    $proc = Start-Process $exe -ArgumentList @('--ui-width=1422') -PassThru
    try {
        $AE = [System.Windows.Automation.AutomationElement]; $Scope = [System.Windows.Automation.TreeScope]
        $win = $null
        for ($i = 0; $i -lt 60 -and -not $win; $i++) { $win = $AE::RootElement.FindFirst($Scope::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id))); Start-Sleep -Milliseconds 500 }
        if (-not $win) { return [pscustomobject]@{ contrast = $null; note = 'the app produced no window'; capture = $null } }
        Start-Sleep -Seconds 9
        $h = [IntPtr]$win.Current.NativeWindowHandle
        [TransWin]::MoveWindow($h, 20, 20, 1422, 800, $true) | Out-Null
        $front = $false
        for ($k = 0; $k -lt 4 -and -not $front; $k++) { [TransWin]::SetForegroundWindow($h) | Out-Null; Start-Sleep -Milliseconds 600; $front = ([TransWin]::GetForegroundWindow() -eq $h) }
        if (-not $front) { return [pscustomobject]@{ contrast = $null; note = 'the app could not be brought to the front, so nothing was captured'; capture = $null } }
        $fr = Get-FrameRect $h $win.Current.BoundingRectangle
        $png = Join-Path $rootFull ("transparency-{0}.png" -f $tag)
        $bm = New-Object System.Drawing.Bitmap ([int]$fr.W), ([int]$fr.H); $g = [System.Drawing.Graphics]::FromImage($bm)
        $g.CopyFromScreen([int]$fr.X, [int]$fr.Y, 0, 0, $bm.Size); $bm.Save($png, [System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bm.Dispose()
        [pscustomobject]@{ contrast = (Measure-Title $png); note = ''; capture = $png }
    }
    finally { Stop-Ours $proc }
}

$states = New-Object System.Collections.Generic.List[object]
$restored = $false; $err = $null
try {
    foreach ($on in 1, 0) {
        Write-Setting $on
        $entry = [ordered]@{ transparencyEffects = $(if ($on) { 'on' } else { 'off' }); registry = (Read-Setting); appSees = $null; captures = @(); gate = $null; verdict = 'PASS'; notes = @() }
        # Ask the app what it sees.
        $d = New-Data "report-$on" 'dark'
        Start-Process $exe -ArgumentList @('--ui-shot', '--ui-width=1422') -PassThru -Wait -WindowStyle Minimized | Out-Null
        $rp = Join-Path $d 'benchmarks\motion-report.json'
        if (Test-Path $rp) { $entry.appSees = (Get-Content $rp -Raw | ConvertFrom-Json).transparencyEffects }
        if ($null -eq $entry.appSees) { $entry.verdict = 'INCONCLUSIVE'; $entry.notes += 'the app did not report the setting' }
        elseif ([bool]$entry.appSees -ne [bool]$on) { $entry.verdict = 'INCONCLUSIVE'; $entry.notes += "the app sees transparency effects as $($entry.appSees), not $([bool]$on): the setting did not reach it" }

        foreach ($theme in 'dark', 'light') {
            $c = Run-Capture "$($entry.transparencyEffects)-$theme" $theme
            $entry.captures += [ordered]@{ theme = $theme; titleContrast = $c.contrast; note = $c.note; file = $c.capture }
            if ($null -eq $c.contrast) { if ($entry.verdict -eq 'PASS') { $entry.verdict = 'INCONCLUSIVE' }; $entry.notes += "$theme`: $($c.note)" }
            elseif ($c.contrast -lt 4.5) { $entry.verdict = 'FAIL'; $entry.notes += "$theme title text contrast $($c.contrast):1 is below 4.5:1" }
        }
        Remove-Item Env:\JEVBROWSE_START_URL, Env:\JEVBROWSE_THEME, Env:\JEVBROWSE_DATA_DIR -ErrorAction SilentlyContinue   # the gate sets its own
        $out = & powershell -NoProfile -ExecutionPolicy Bypass -File $gate -Configuration $Configuration -Width 1422 -Sidebar open -Root (Join-Path $rootFull "tr-gate-$on") 2>&1 | Out-String
        $code = $LASTEXITCODE; $j = try { $out | ConvertFrom-Json } catch { $null }
        $judged = Judge-Gate $code $j
        $entry.gate = [ordered]@{ exit = $code; verdict = $j.verdict; judged = $judged; panelCheck = $j.panelCheck; failures = @($j.failures); error = $(if ($judged -eq 'ERROR' -and $null -eq $j) { 'the gate produced no readable report' } else { $j.error }) }
        $entry.verdict = Merge-Verdict $entry.verdict $judged
        $states.Add($entry)
    }
}
catch { $err = $_.Exception.Message + ' at line ' + $_.InvocationInfo.ScriptLineNumber }
finally {
    try { Write-Setting $original; $now = Read-Setting; $restored = if ($null -eq $original) { $null -eq $now } else { $now -eq $original } } catch { $restored = $false }
    if ($restored) { ([ordered]@{ state = 'restored'; existed = ($null -ne $original); value = $original; recordedAt = (Get-Date).ToString('s') }) | ConvertTo-Json | Set-Content $marker -Encoding utf8 }
}
$vs = @($states | ForEach-Object { $_.verdict })
$verdict = if ($err -or -not $restored) { 'ERROR' } elseif ($vs -contains 'ERROR') { 'ERROR' } elseif ($vs -contains 'FAIL') { 'FAIL' } elseif ($vs.Count -lt 2 -or $vs -contains 'INCONCLUSIVE') { 'INCONCLUSIVE' } else { 'PASS' }
$script:exitCode = switch ($verdict) { 'PASS' { 0 } 'FAIL' { 1 } 'INCONCLUSIVE' { 2 } default { 3 } }
$report = [ordered]@{ verdict = $verdict; originalTransparency = $(if ($null -eq $original) { 'not set (Windows default)' } else { "$original" }); originalRestoredAndConfirmed = $restored; error = $err; states = $states }
$report | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $rootFull 'transparency-report.json') -Encoding utf8
$report | ConvertTo-Json -Depth 8
exit $script:exitCode
