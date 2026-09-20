<#
.SYNOPSIS
  Launches the real app and checks what a screen reader and a keyboard user actually get.

.DESCRIPTION
  Drives the running window through Windows UI Automation, which is the tree Narrator reads. Reports FAIL if:
    - an interactive control (button, dropdown, text box) has no accessible name,
    - one is named after a raw glyph character (an icon button announced as a private-use symbol),
    - one is not reached by forward Tab during a full cycle,
    - a control the toolbar is required to expose is missing.

  Exit codes, and only these:
    0  PASS          every check ran, the Tab cycle was proven complete, and nothing failed.
    1  FAIL          at least one check failed.
    2  INCONCLUSIVE  nothing failed, but the Tab cycle was NOT proven complete (focus left the app, the app could not be
                     brought to the front, or the key limit ran out before the cycle wrapped). Not a pass.
    3  ERROR         the run itself broke (no window, refused path, ...).

  What it touches, so it is safe to run on a machine that is in use:
    - It ONLY ever stops the process it launched and that process's descendants. It never stops other JevBrowse
      instances or any other program's WebView2 processes.
    - It ONLY ever creates a new, uniquely named directory under a fixed test root, and never deletes anything: the
      directory it makes must not already exist, and its resolved path must be a direct child of the root.
  It launches a real window and sends real keystrokes, so do not type elsewhere while it runs.

  It does not test contrast, zoom, high-contrast themes, or a real screen reader.

.PARAMETER Configuration   debug (default) or release build to launch.
.PARAMETER Root            Fixed test root. Each run gets its own new subdirectory under it.
.PARAMETER MaxTabPresses   Upper bound on Tab presses; running out is INCONCLUSIVE, never a pass.
#>
param(
    [string]$Configuration = 'debug',
    [string]$Root = 'D:\Browser\_ui-check',
    [int]$Width = 1422,
    [int]$MaxTabPresses = 45
)
$ErrorActionPreference = 'Stop'
$script:exitCode = 3

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

# ---- a fresh, uniquely named directory that did not exist a moment ago, directly under the fixed root ----
try {
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    if ($rootFull.Length -lt 8 -or $rootFull -match '^[A-Za-z]:$') { throw "Refusing test root '$rootFull': it is a drive root or too short to be a scratch directory." }
    New-Item -ItemType Directory -Force $rootFull | Out-Null
    $runName = 'run-{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), ([guid]::NewGuid().ToString('N').Substring(0, 8))
    $Out = [IO.Path]::GetFullPath((Join-Path $rootFull $runName))
    if ((Split-Path $Out -Parent) -ne $rootFull) { throw "Run directory '$Out' is not a direct child of '$rootFull'." }
    if (Test-Path -LiteralPath $Out) { throw "Run directory '$Out' already exists; refusing to reuse it." }
    New-Item -ItemType Directory $Out, "$Out\shots" | Out-Null

}
catch {
    # A refused path is an ERROR (3), never a FAIL (1): the two must not share an exit code.
    [ordered]@{ verdict = 'ERROR'; pass = $false; error = $_.Exception.Message } | ConvertTo-Json
    exit 3
}

$env:JEVBROWSE_DATA_DIR = $Out
$env:JEVBROWSE_NO_FILTER_UPDATE = '1'; $env:JEVBROWSE_AI = '0'; $env:JEVBROWSE_MODE = 'Power'; $env:JEVBROWSE_DEVSPACE = '0'
'{"firstRunDone":true}' | Set-Content "$Out\settings.json" -Encoding ascii

# ---- launch, and remember exactly what we launched ----
$exe = Join-Path $PSScriptRoot "..\artifacts\bin\JevBrowse.App\${Configuration}_win-x64\JevBrowse.App.exe"
$proc = Start-Process (Resolve-Path $exe) -PassThru

function Get-Descendants([int]$rootPid) {
    $all = @(Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId)
    $found = New-Object System.Collections.Generic.List[int]
    $queue = New-Object System.Collections.Generic.Queue[int]; $queue.Enqueue($rootPid)
    while ($queue.Count -gt 0) {
        $p = $queue.Dequeue()
        foreach ($c in ($all | Where-Object { $_.ParentProcessId -eq $p })) { if (-not $found.Contains([int]$c.ProcessId)) { $found.Add([int]$c.ProcessId); $queue.Enqueue([int]$c.ProcessId) } }
    }
    $found
}
function Stop-OurTree {
    # Children first, then the app. Nothing that is not a descendant of the process WE started is ever touched.
    foreach ($id in (Get-Descendants $proc.Id)) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue }
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
}

try {
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
        foreach ($t in @($CT::Button, $CT::ComboBox, $CT::Edit)) {
            $c = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $t)
            foreach ($e in $win.FindAll($Scope::Descendants, $c)) {
                if ($e.Current.IsOffscreen) { continue }
                [pscustomobject]@{ type = $t.ProgrammaticName.Replace('ControlType.', ''); id = $e.Current.AutomationId; name = $e.Current.Name; focusable = $e.Current.IsKeyboardFocusable }
            }
        }
    }
    $all = @(Interactive)
    $failures = New-Object System.Collections.Generic.List[string]

    foreach ($e in $all) {
        $label = if ($e.id) { $e.id } else { "$($e.type) '$($e.name)'" }
        if ([string]::IsNullOrWhiteSpace($e.name)) { $failures.Add("UNNAMED: $($e.type) $label"); continue }
        # Private-use codepoints (Segoe MDL2 icons) announce as nothing or as a box; a name must be words.
        if ($e.name -match '[\uE000-\uF8FF]') { $failures.Add("GLYPH-NAMED: $($e.type) $label -> a private-use symbol") }
    }

    $required = @('Back', 'Forward', 'Reload', 'Hide sidebar', 'Press to change how this site is treated', 'Shield: blocked requests and site repair',
                  'Explain why this tab is awake or asleep', 'Receipt: what this site did', 'This tab menu', 'Tools menu', 'Command palette', 'Help and welcome')
    $have = @($all | ForEach-Object { $_.name })
    foreach ($r in $required) {
        if (-not ($have | Where-Object { $_ -eq $r -or $_ -like "*$r*" })) { $failures.Add("MISSING: '$r'") }
    }

    # ---- forward Tab: prove the cycle completes, or say plainly that it was not proven ----
    $addr = $win.FindFirst($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'AddressBox')))
    $shell = New-Object -ComObject WScript.Shell
    function Ensure-Foreground {
        for ($k = 0; $k -lt 4; $k++) {
            if ([A11yWin]::GetForegroundWindow() -eq $h) { return $true }
            $shell.AppActivate($proc.Id) | Out-Null; Start-Sleep -Milliseconds 400
        }
        return ([A11yWin]::GetForegroundWindow() -eq $h)
    }
    $reached = New-Object System.Collections.Generic.List[string]
    $tabStatus = 'not started'
    $cycleComplete = $false
    if (-not (Ensure-Foreground)) { $tabStatus = 'INCONCLUSIVE: the app could not be brought to the front, so no key was sent' }
    else {
        $addr.SetFocus(); Start-Sleep -Milliseconds 500
        $tabStatus = "INCONCLUSIVE: $MaxTabPresses Tab presses did not wrap back to the address bar"
        for ($i = 0; $i -lt $MaxTabPresses; $i++) {
            if (-not (Ensure-Foreground)) { $tabStatus = 'INCONCLUSIVE: the OS moved the foreground away from the app mid-cycle'; break }
            [System.Windows.Forms.SendKeys]::SendWait('{TAB}'); Start-Sleep -Milliseconds 250
            $f = $AE::FocusedElement
            if ($f.Current.ProcessId -ne $proc.Id) { $tabStatus = 'INCONCLUSIVE: keyboard focus left the app'; break }
            $reached.Add($f.Current.Name)
            # The ONLY way to a "complete" cycle: focus came back to where it started, after passing other controls.
            if ($i -gt 2 -and $f.Current.AutomationId -eq 'AddressBox') { $cycleComplete = $true; $tabStatus = 'complete'; break }
        }
    }
    if ($cycleComplete) {
        foreach ($e in $all | Where-Object { $_.focusable -and $_.name -and $_.name -notin @('Minimize', 'Maximize', 'Close') }) {
            if ($e.name -notin $reached -and $e.id -ne 'AddressBox') { $failures.Add("NOT REACHED BY TAB: $($e.type) '$($e.name)'") }
        }
    }

    $r = $win.Current.BoundingRectangle
    $bmp = New-Object System.Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size)
    $bmp.Save("$Out\shots\main.png", [System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose()

    $verdict = if ($failures.Count -gt 0) { 'FAIL' } elseif (-not $cycleComplete) { 'INCONCLUSIVE' } else { 'PASS' }
    $script:exitCode = switch ($verdict) { 'PASS' { 0 } 'FAIL' { 1 } default { 2 } }
    $report = [ordered]@{
        verdict = $verdict
        pass = ($verdict -eq 'PASS')
        runDirectory = $Out
        interactiveControls = $all.Count
        tabTraversal = $tabStatus
        tabOrder = $reached
        failures = $failures
    }
    $report | ConvertTo-Json -Depth 4 | Set-Content "$Out\ui-a11y-report.json" -Encoding utf8
    $report | ConvertTo-Json -Depth 4
}
catch {
    $script:exitCode = 3
    [ordered]@{ verdict = 'ERROR'; pass = $false; runDirectory = $Out; error = $_.Exception.Message } | ConvertTo-Json | Tee-Object -FilePath "$Out\ui-a11y-report.json"
}
finally { Stop-OurTree }
exit $script:exitCode
