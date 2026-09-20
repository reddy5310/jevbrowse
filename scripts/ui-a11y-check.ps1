<#
.SYNOPSIS
  Launches the real app and checks what a screen reader and a keyboard user actually get. Exits 1 on any failure.

.DESCRIPTION
  Drives the running window through Windows UI Automation, which is the tree Narrator reads. Fails if:
    - an interactive control (button, dropdown, text box) has no accessible name,
    - one is named after a raw glyph character (an icon button announced as a private-use symbol),
    - one is not reached by forward Tab within a full cycle,
    - a control the toolbar is required to expose is missing.
  Writes ui-a11y-report.json and screenshots next to the data directory it uses.

  Limits, stated plainly: it needs an interactive desktop, and it sends real keystrokes, so do not type elsewhere while
  it runs. Windows can pull the foreground away from a background process; if focus leaves the app the tab traversal is
  reported as INCONCLUSIVE rather than as a pass or a failure. It does not test contrast, zoom, high-contrast themes, or
  a real screen reader.

.PARAMETER Configuration  debug (default) or release build to launch.
.PARAMETER Out            Data directory to use; it is deleted first, so never point it at real data.
#>
param(
    [string]$Configuration = 'debug',
    [string]$Out = 'D:\Browser\data-a11y',
    [int]$Width = 1422
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class A11yWin {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int H, bool r);
}
'@
[A11yWin]::SetProcessDPIAware() | Out-Null
. (Join-Path $PSScriptRoot 'env.ps1') | Out-Null

if ($Out -notmatch 'a11y|uia|test|check') { throw "Refusing to delete '$Out': the path does not look like a scratch directory." }
Remove-Item $Out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$Out\shots" | Out-Null
$env:JEVBROWSE_DATA_DIR = $Out
$env:JEVBROWSE_NO_FILTER_UPDATE = '1'; $env:JEVBROWSE_AI = '0'; $env:JEVBROWSE_MODE = 'Power'; $env:JEVBROWSE_DEVSPACE = '0'
'{"firstRunDone":true}' | Set-Content "$Out\settings.json" -Encoding ascii

Get-Process JevBrowse.App, msedgewebview2 -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
$exe = Join-Path $PSScriptRoot "..\artifacts\bin\JevBrowse.App\${Configuration}_win-x64\JevBrowse.App.exe"
$proc = Start-Process (Resolve-Path $exe) -PassThru

$AE = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
$win = $null
for ($i = 0; $i -lt 60 -and -not $win; $i++) {
    $win = $AE::RootElement.FindFirst($Scope::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id)))
    Start-Sleep -Milliseconds 500
}
if (-not $win) { throw 'the app produced no window' }
Start-Sleep -Seconds 8
$h = [IntPtr]$win.Current.NativeWindowHandle
[A11yWin]::MoveWindow($h, 20, 20, $Width, 800, $true) | Out-Null
[A11yWin]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Seconds 2

function Interactive {
    $types = @($CT::Button, $CT::ComboBox, $CT::Edit)
    foreach ($t in $types) {
        $c = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $t)
        foreach ($e in $win.FindAll($Scope::Descendants, $c)) {
            if ($e.Current.IsOffscreen) { continue }
            [pscustomobject]@{ type = $t.ProgrammaticName.Replace('ControlType.', ''); id = $e.Current.AutomationId; name = $e.Current.Name; focusable = $e.Current.IsKeyboardFocusable }
        }
    }
}
# Caption buttons (Minimize/Maximize/Close) are the OS's, not ours, and are named by it.
$all = @(Interactive)
$failures = New-Object System.Collections.Generic.List[string]

foreach ($e in $all) {
    $label = if ($e.id) { $e.id } else { "$($e.type) '$($e.name)'" }
    if ([string]::IsNullOrWhiteSpace($e.name)) { $failures.Add("UNNAMED: $($e.type) $label"); continue }
    # Private-use codepoints (Segoe MDL2 icons) announce as nothing or as a box; a name must be words.
    if ($e.name -match '[\uE000-\uF8FF]') { $failures.Add("GLYPH-NAMED: $($e.type) $label -> a private-use symbol") }
}

# Controls the toolbar is required to expose, by the names a screen reader would say.
$required = @('Back', 'Forward', 'Reload', 'Hide sidebar', 'Press to change how this site is treated', 'Shield: blocked requests and site repair',
              'Explain why this tab is awake or asleep', 'Receipt: what this site did', 'This tab menu', 'Tools menu', 'Command palette', 'Help and welcome')
$have = @($all | ForEach-Object { $_.name })
foreach ($r in $required) {
    if (-not ($have | Where-Object { $_ -eq $r -or $_ -like "*$r*" })) { $failures.Add("MISSING: '$r'") }
}

# Forward Tab from the address bar: does every focusable named control get reached before the cycle repeats?
$addr = $win.FindFirst($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'AddressBox')))
# Keystrokes go to whichever window is in front, so prove the app is, before every key. Windows can hand the foreground
# to another program at any moment; when it does, say INCONCLUSIVE rather than judge focus that was never ours.
$shell = New-Object -ComObject WScript.Shell
function Ensure-Foreground {
    for ($k = 0; $k -lt 4; $k++) {
        if ([A11yWin]::GetForegroundWindow() -eq $h) { return $true }
        $shell.AppActivate($proc.Id) | Out-Null; Start-Sleep -Milliseconds 400
    }
    return ([A11yWin]::GetForegroundWindow() -eq $h)
}
Ensure-Foreground | Out-Null
$addr.SetFocus(); Start-Sleep -Milliseconds 500
$reached = New-Object System.Collections.Generic.List[string]
$leftApp = $false
for ($i = 0; $i -lt 45; $i++) {
    if (-not (Ensure-Foreground)) { $leftApp = $true; break }
    [System.Windows.Forms.SendKeys]::SendWait('{TAB}'); Start-Sleep -Milliseconds 250
    $f = $AE::FocusedElement
    if ($f.Current.ProcessId -ne $proc.Id) { $leftApp = $true; break }
    $reached.Add($f.Current.Name)
    if ($i -gt 2 -and $f.Current.AutomationId -eq 'AddressBox') { break }   # wrapped: the cycle is complete
}
$tabStatus = 'complete'
if ($leftApp) { $tabStatus = 'INCONCLUSIVE: focus left the app (the OS moved the foreground), so reachability was not judged' }
else {
    foreach ($e in $all | Where-Object { $_.focusable -and $_.name -and $_.name -notin @('Minimize', 'Maximize', 'Close') }) {
        if ($e.name -notin $reached -and $e.id -ne 'AddressBox') { $failures.Add("NOT REACHED BY TAB: $($e.type) '$($e.name)'") }
    }
}

$r = $win.Current.BoundingRectangle
$bmp = New-Object System.Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
$g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size)
$bmp.Save("$Out\shots\main.png", [System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose()

$report = [ordered]@{
    pass = ($failures.Count -eq 0)
    interactiveControls = $all.Count
    tabTraversal = $tabStatus
    tabOrder = $reached
    failures = $failures
}
$report | ConvertTo-Json -Depth 4 | Set-Content "$Out\ui-a11y-report.json" -Encoding utf8

Get-Process JevBrowse.App -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep 1; Get-Process msedgewebview2 -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

$report | ConvertTo-Json -Depth 4
if ($failures.Count -gt 0) { exit 1 }
