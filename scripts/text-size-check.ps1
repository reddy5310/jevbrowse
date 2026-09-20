<#
.SYNOPSIS
  Runs the UI checks at larger Windows "Text size" values, then puts the setting back.

.DESCRIPTION
  Text size (Settings > Accessibility > Text size) scales text only; it is separate from display scaling, which this script
  never touches. For each factor it: sets the value, confirms the APP sees it (the app reports UISettings.TextScaleFactor),
  runs the accessibility/layout gate at two widths (clipped, squeezed and unreachable controls, panels open, keyboard focus),
  shows the permission and New-workspace decision dialogs and checks every button is on screen, and saves window captures.

  SAFETY: the original value (or its absence) is written to <Root>\textsize-original.json BEFORE anything changes, and is
  restored in a finally block and read back. If the script is killed before that, run it again with -Restore. Only the one
  registry value HKCU\Software\Microsoft\Accessibility\TextScaleFactor is touched.

  Exit: 0 every factor clean; 1 a defect was found; 2 inconclusive (foreground unavailable or the app did not see the
  setting); 3 error, or the original setting could not be confirmed restored.
#>
param(
    [string]$Root = 'D:\Browser\_ui-check',
    # Windows offers 100 to 225. Anything else is refused before the registry is touched (a comma list through -File once arrived as 125150).
    [ValidateRange(100, 225)][int[]]$Factors = @(125, 150, 225),
    [ValidateRange(400, 4000)][int[]]$Widths = @(1422, 700),
    [switch]$Restore,
    [string]$Configuration = 'debug'
)
$ErrorActionPreference = 'Stop'
$script:exitCode = 3
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class TextSizeWin {
  [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
  public static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, string l, uint flags, uint timeout, out IntPtr result);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int H, bool r);
}
'@
[TextSizeWin]::SetProcessDPIAware() | Out-Null

$rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\')
if ($Root -notmatch '^[A-Za-z]:[\\/]' -or $rootFull.Length -lt 8) { [ordered]@{ verdict = 'ERROR'; error = "Refusing test root '$Root'" } | ConvertTo-Json; exit 3 }
New-Item -ItemType Directory -Force $rootFull | Out-Null
$key = 'HKCU:\Software\Microsoft\Accessibility'
$name = 'TextScaleFactor'
$marker = Join-Path $rootFull 'textsize-original.json'

function Read-Setting { $p = Get-ItemProperty -Path $key -Name $name -ErrorAction SilentlyContinue; if ($null -eq $p) { $null } else { [int]$p.$name } }
function Broadcast { $r = [IntPtr]::Zero; [TextSizeWin]::SendMessageTimeout([IntPtr]0xFFFF, 0x1A, [IntPtr]::Zero, 'Accessibility', 2, 2000, [ref]$r) | Out-Null }
function Write-Setting($value) {
    if ($null -eq $value) { Remove-ItemProperty -Path $key -Name $name -ErrorAction SilentlyContinue }
    else { if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }; Set-ItemProperty -Path $key -Name $name -Value ([int]$value) -Type DWord }
    Broadcast; Start-Sleep -Milliseconds 800
}

if ($Restore) {
    if (-not (Test-Path $marker)) { [ordered]@{ verdict = 'ERROR'; error = 'no textsize-original.json: nothing to restore' } | ConvertTo-Json; exit 3 }
    $o = Get-Content $marker -Raw | ConvertFrom-Json
    Write-Setting $(if ($o.existed) { [int]$o.value } else { $null })
    $now = Read-Setting
    $ok = if ($o.existed) { $now -eq [int]$o.value } else { $null -eq $now }
    [ordered]@{ verdict = $(if ($ok) { 'RESTORED' } else { 'ERROR' }); original = $o; nowInRegistry = $now } | ConvertTo-Json
    exit $(if ($ok) { 0 } else { 3 })
}

$original = Read-Setting
[ordered]@{ existed = ($null -ne $original); value = $original; recordedAt = (Get-Date).ToString('s') } | ConvertTo-Json | Set-Content $marker -Encoding utf8

$exe = Resolve-Path (Join-Path $PSScriptRoot "..\artifacts\bin\JevBrowse.App\${Configuration}_win-x64\JevBrowse.App.exe")
$gate = Join-Path $PSScriptRoot 'ui-a11y-check.ps1'
$results = New-Object System.Collections.Generic.List[object]
$restored = $false; $err = $null

function Get-Tree([int]$rootPid) {
    $all = @(Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId); $found = New-Object System.Collections.Generic.List[int]
    $q = New-Object System.Collections.Generic.Queue[int]; $q.Enqueue($rootPid)
    while ($q.Count) { $p = $q.Dequeue(); foreach ($c in ($all | Where-Object { $_.ParentProcessId -eq $p })) { if (-not $found.Contains([int]$c.ProcessId)) { $found.Add([int]$c.ProcessId); $q.Enqueue([int]$c.ProcessId) } } }
    $found
}
function Stop-Ours($proc) { foreach ($id in (Get-Tree $proc.Id)) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue }; Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }

# What the app itself reports it sees, so a setting that never reached the app is INCONCLUSIVE, not "fine at that size".
function New-Data([string]$tag) {
    $d = Join-Path $rootFull ('ts-{0}-{1}' -f $tag, [guid]::NewGuid().ToString('N').Substring(0, 8)); New-Item -ItemType Directory $d | Out-Null
    '{"firstRunDone":true}' | Set-Content "$d\settings.json" -Encoding ascii
    $env:JEVBROWSE_DATA_DIR = $d; $env:JEVBROWSE_NO_FILTER_UPDATE = '1'; $env:JEVBROWSE_AI = '0'; $env:JEVBROWSE_MODE = 'Power'; $env:JEVBROWSE_THEME = 'dark'; $env:JEVBROWSE_START_URL = 'jev://welcome/'
    $d
}

function Run-Shot([string]$tag, [int]$width, [string]$panel) {
    $d = New-Data $tag
    $a = @('--ui-shot', "--ui-width=$width", '--ui-sidebar=open'); if ($panel) { $a += "--ui-panel=$panel" }
    Start-Process $exe -ArgumentList $a -PassThru -Wait -WindowStyle Minimized | Out-Null
    $scale = $null; $r = Join-Path $d 'benchmarks\motion-report.json'
    if (Test-Path $r) { $scale = (Get-Content $r -Raw | ConvertFrom-Json).textScaleFactor }
    $png = Join-Path $d 'benchmarks\ui-shot.png'; $saved = $null
    if (Test-Path $png) { $saved = Join-Path $rootFull ("textsize-{0}.png" -f $tag); Copy-Item $png $saved -Force }
    [pscustomobject]@{ appSeesScale = $scale; capture = $saved }
}

function Run-Dialog([string]$which, [int]$width) {
    $d = New-Data "dlg-$which"
    $proc = Start-Process $exe -ArgumentList @("--ui-dialog=$which", "--ui-width=$width", '--ui-sidebar=open') -PassThru
    $problems = New-Object System.Collections.Generic.List[string]; $seen = @(); $scale = $null
    try {
        $AE = [System.Windows.Automation.AutomationElement]; $Scope = [System.Windows.Automation.TreeScope]
        $win = $null
        for ($i = 0; $i -lt 60 -and -not $win; $i++) { $win = $AE::RootElement.FindFirst($Scope::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id))); Start-Sleep -Milliseconds 500 }
        if (-not $win) { return [pscustomobject]@{ dialog = $which; verdict = 'INCONCLUSIVE'; note = 'the app produced no window' } }
        Start-Sleep -Seconds 13
        $rep = Join-Path $d 'benchmarks\dialog-report.json'; if (Test-Path $rep) { $scale = (Get-Content $rep -Raw | ConvertFrom-Json).textScaleFactor }
        $wb = $win.Current.BoundingRectangle
        $expect = if ($which -eq 'permission') { @('Allow', 'Block this site', 'Not now') } else { @('Create', 'Cancel') }
        $buttons = @($win.FindAll($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button))))
        foreach ($b in $buttons) { $seen += $b.Current.Name }
        $found = 0
        foreach ($want in $expect) {
            $b = $buttons | Where-Object { $_.Current.Name -eq $want -or $_.Current.Name -like "$want*" } | Select-Object -First 1
            if (-not $b) { $problems.Add("BUTTON MISSING: '$want'"); continue }
            $found++
            $r = $b.Current.BoundingRectangle
            if ($b.Current.IsOffscreen -or [double]::IsInfinity($r.Width) -or $r.Width -lt 8 -or $r.Height -lt 8) { $problems.Add("BUTTON NOT VISIBLE: '$want'"); continue }
            if ($r.Left -lt $wb.Left - 1 -or $r.Right -gt $wb.Right + 1 -or $r.Top -lt $wb.Top - 1 -or $r.Bottom -gt $wb.Bottom + 1) { $problems.Add("BUTTON CLIPPED: '$want' x=$([int]$r.Left)..$([int]$r.Right) window x=$([int]$wb.Left)..$([int]$wb.Right)") }
        }
        $png = Join-Path $rootFull ("textsize-dialog-{0}-{1}.png" -f $which, [guid]::NewGuid().ToString('N').Substring(0, 6))
        $h = [IntPtr]$win.Current.NativeWindowHandle
        [TextSizeWin]::SetForegroundWindow($h) | Out-Null; Start-Sleep -Milliseconds 700
        $captured = $null
        if ([TextSizeWin]::GetForegroundWindow() -eq $h) {
            $bm = New-Object System.Drawing.Bitmap ([int]$wb.Width), ([int]$wb.Height); $g = [System.Drawing.Graphics]::FromImage($bm)
            $g.CopyFromScreen([int]$wb.X, [int]$wb.Y, 0, 0, $bm.Size); $bm.Save($png, [System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bm.Dispose(); $captured = $png
        }
        $verdict = if ($problems.Count) { 'FAIL' } elseif ($found -lt $expect.Count) { 'INCONCLUSIVE' } else { 'PASS' }
        [pscustomobject]@{ dialog = $which; width = $width; verdict = $verdict; appSeesScale = $scale; problems = @($problems); capture = $captured; buttonsSeen = ($seen -join ' | ') }
    }
    finally { Stop-Ours $proc }
}

try {
    foreach ($f in $Factors) {
        Write-Setting $f
        $now = Read-Setting
        $entry = [ordered]@{ textSizePercent = $f; registry = $now; gate = @(); dialogs = @(); captures = @(); appSeesScale = $null; verdict = 'PASS'; notes = @() }
        if ($now -ne $f) { $entry.verdict = 'ERROR'; $entry.notes += "registry reads $now after setting $f"; $results.Add($entry); continue }

        foreach ($w in $Widths) {
            foreach ($panel in @('', 'receipt', 'search')) {
                $s = Run-Shot ("{0}-{1}{2}" -f $f, $w, $(if ($panel) { "-$panel" } else { '' })) $w $panel
                if ($s.capture) { $entry.captures += $s.capture }
                if ($null -eq $entry.appSeesScale -and $null -ne $s.appSeesScale) { $entry.appSeesScale = [double]$s.appSeesScale }
            }
        }
        if ($null -eq $entry.appSeesScale) { $entry.verdict = 'INCONCLUSIVE'; $entry.notes += 'the app did not report a text scale, so the setting is not proven to have reached it' }
        elseif ([math]::Abs($entry.appSeesScale * 100 - $f) -gt 1) { $entry.verdict = 'INCONCLUSIVE'; $entry.notes += "the app sees $([math]::Round($entry.appSeesScale * 100))%, not $f%: the setting did not reach it" }

        foreach ($w in $Widths) {
            foreach ($which in 'permission', 'workspace') {
                $d = Run-Dialog $which $w; $entry.dialogs += $d
                if ($d.verdict -eq 'FAIL') { $entry.verdict = 'FAIL' } elseif ($d.verdict -eq 'INCONCLUSIVE' -and $entry.verdict -eq 'PASS') { $entry.verdict = 'INCONCLUSIVE' }
            }
            foreach ($sb in 'open', 'hidden') {
                # The gate sets its own start page and theme; ours must not leak into it (it once did, and the gate then looked for a tab that was not there).
                Remove-Item Env:\JEVBROWSE_START_URL, Env:\JEVBROWSE_THEME, Env:\JEVBROWSE_DATA_DIR -ErrorAction SilentlyContinue
                $out = & powershell -NoProfile -ExecutionPolicy Bypass -File $gate -Width $w -Sidebar $sb -Root (Join-Path $rootFull "ts-gate-$f-$w-$sb") 2>&1 | Out-String
                $code = $LASTEXITCODE; $j = try { $out | ConvertFrom-Json } catch { $null }
                $entry.gate += [ordered]@{ width = $w; sidebar = $sb; exit = $code; verdict = $j.verdict; panelCheck = $j.panelCheck; failures = @($j.failures); error = $j.error }
                if ($code -eq 1) { $entry.verdict = 'FAIL' } elseif ($code -ne 0 -and $entry.verdict -eq 'PASS') { $entry.verdict = 'INCONCLUSIVE' }
            }
        }
        $results.Add($entry)
    }
}
catch { $err = $_.Exception.Message + ' at line ' + $_.InvocationInfo.ScriptLineNumber }
finally {
    try { Write-Setting $original; $now = Read-Setting; $restored = if ($null -eq $original) { $null -eq $now } else { $now -eq $original } } catch { $restored = $false }
}
$vs = @($results | ForEach-Object { $_.verdict })
$verdict = if ($err -or -not $restored -or ($vs -contains 'ERROR')) { 'ERROR' } elseif ($vs -contains 'FAIL') { 'FAIL' } elseif ($vs.Count -eq 0 -or $vs -contains 'INCONCLUSIVE' -or $vs.Count -lt $Factors.Count) { 'INCONCLUSIVE' } else { 'PASS' }
$script:exitCode = switch ($verdict) { 'PASS' { 0 } 'FAIL' { 1 } 'INCONCLUSIVE' { 2 } default { 3 } }
$report = [ordered]@{ verdict = $verdict; originalTextSize = $(if ($null -eq $original) { 'not set (Windows default, 100%)' } else { "$original%" }); originalRestoredAndConfirmed = $restored; error = $err; factors = $results }
$report | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $rootFull 'textsize-report.json') -Encoding utf8
$report | ConvertTo-Json -Depth 8
exit $script:exitCode
