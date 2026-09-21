<#
.SYNOPSIS
  Observes, from OUTSIDE the app, whether an agent's hidden screenshot ever shows the page or moves focus.

.DESCRIPTION
  Launches the app in --agent-screenshot-stage-check mode, which takes several agent screenshots of a page that is not shown while the
  person's own page (the welcome page) sits still. This script captures the app window from the screen repeatedly the whole time and
  compares every frame with a baseline taken before the first capture. If the agent's page were drawn where the person could see it,
  even for a moment, frames taken during a capture would differ from the baseline in the page area. It also records the foreground
  window before and after, and how many frames fell inside capture windows (a sampling limit, reported honestly).

  Positive control: run it against a build that stages the page ON canvas; it must then report FAIL. A check that cannot fail proves nothing.

  A screen capture is only taken while the app is verifiably the foreground window, so it never photographs another program; if that
  cannot be arranged the result is INCONCLUSIVE.

  Exit: 0 clean; 1 the page was seen (or focus moved); 2 inconclusive; 3 error.
#>
param(
    [string]$Root = 'D:\Browser\_ui-check',
    [string]$Configuration = 'debug',
    [int]$TolerancePixels = 40,
    # Keep every Nth frame as a PNG in the run directory, so a person can look at what was actually observed.
    [int]$SaveEveryNth = 0
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ProcessScope.ps1')
$script:exitCode = 3
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type -ReferencedAssemblies System.Drawing @'
using System; using System.Drawing; using System.Drawing.Imaging; using System.Runtime.InteropServices;
public static class Stage {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int H, bool r);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  // Fraction of the region's pixels that differ noticeably from its top-left pixel. A picture of a real window has text and cards; a blank
  // (black, minimized, off-screen or otherwise uncapturable) frame has none. Used to refuse to judge from frames that show nothing.
  public static double NonUniformFraction(Bitmap a, int x0, int y0, int x1, int y1) {
    var d = a.LockBits(new Rectangle(0, 0, a.Width, a.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    long n = 0, total = 0;
    try {
      int rb = Marshal.ReadByte(d.Scan0, y0 * d.Stride + x0 * 4), rg = Marshal.ReadByte(d.Scan0, y0 * d.Stride + x0 * 4 + 1), rr = Marshal.ReadByte(d.Scan0, y0 * d.Stride + x0 * 4 + 2);
      for (int y = y0; y < y1; y += 2) for (int x = x0; x < x1; x += 2) {
        int i = y * d.Stride + x * 4; total++;
        if (Math.Abs(Marshal.ReadByte(d.Scan0, i) - rb) + Math.Abs(Marshal.ReadByte(d.Scan0, i + 1) - rg) + Math.Abs(Marshal.ReadByte(d.Scan0, i + 2) - rr) > 24) n++;
      }
    } finally { a.UnlockBits(d); }
    return total == 0 ? 0 : (double)n / total;
  }
  // How many pixels in the region differ by more than a small amount. Fast, so many frames can be compared while a capture runs.
  public static int Diff(Bitmap a, Bitmap b, int x0, int y0, int x1, int y1, int threshold) {
    var ra = new Rectangle(0, 0, a.Width, a.Height); var rb = new Rectangle(0, 0, b.Width, b.Height);
    var da = a.LockBits(ra, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb); var db = b.LockBits(rb, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    int n = 0;
    try {
      for (int y = y0; y < y1; y++) {
        for (int x = x0; x < x1; x++) {
          int ia = y * da.Stride + x * 4, ib = y * db.Stride + x * 4;
          int d = Math.Abs(Marshal.ReadByte(da.Scan0, ia) - Marshal.ReadByte(db.Scan0, ib)) + Math.Abs(Marshal.ReadByte(da.Scan0, ia + 1) - Marshal.ReadByte(db.Scan0, ib + 1)) + Math.Abs(Marshal.ReadByte(da.Scan0, ia + 2) - Marshal.ReadByte(db.Scan0, ib + 2));
          if (d > threshold) n++;
        }
      }
    } finally { a.UnlockBits(da); b.UnlockBits(db); }
    return n;
  }
}
'@
[Stage]::SetProcessDPIAware() | Out-Null

$rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\')
if ($Root -notmatch '^[A-Za-z]:[\\/]' -or $rootFull.Length -lt 8) { [ordered]@{ verdict = 'ERROR'; error = "Refusing test root '$Root'" } | ConvertTo-Json; exit 3 }
$exe = Resolve-Path (Join-Path $PSScriptRoot "..\artifacts\bin\JevBrowse.App\${Configuration}_win-x64\JevBrowse.App.exe")
$d = Join-Path $rootFull ('stage-' + [guid]::NewGuid().ToString('N').Substring(0, 8)); New-Item -ItemType Directory -Force "$d\benchmarks" | Out-Null
'{"firstRunDone":true}' | Set-Content "$d\settings.json" -Encoding ascii
$env:JEVBROWSE_DATA_DIR = $d; $env:JEVBROWSE_NO_FILTER_UPDATE = '1'; $env:JEVBROWSE_AI = '0'; $env:JEVBROWSE_MODE = 'Power'; $env:JEVBROWSE_THEME = 'dark'; $env:JEVBROWSE_START_URL = 'jev://welcome/'

function Get-Tree([int]$rootPid) {
    $all = @(Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId); $found = New-Object System.Collections.Generic.List[int]
    $q = New-Object System.Collections.Generic.Queue[int]; $q.Enqueue($rootPid)
    while ($q.Count) { $p = $q.Dequeue(); foreach ($c in ($all | Where-Object { $_.ParentProcessId -eq $p })) { if (-not $found.Contains([int]$c.ProcessId)) { $found.Add([int]$c.ProcessId); $q.Enqueue([int]$c.ProcessId) } } }
    $found
}
$proc = Start-Process $exe -ArgumentList '--agent-screenshot-stage-check' -PassThru
$report = $null
try {
    $AE = [System.Windows.Automation.AutomationElement]; $Scope = [System.Windows.Automation.TreeScope]
    $win = $null
    for ($i = 0; $i -lt 60 -and -not $win; $i++) { $win = $AE::RootElement.FindFirst($Scope::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id))); Start-Sleep -Milliseconds 500 }
    if (-not $win) { throw 'the app produced no window' }
    $h = [IntPtr]$win.Current.NativeWindowHandle
    if ([Stage]::IsIconic($h)) { [Stage]::ShowWindow($h, 9) | Out-Null; Start-Sleep -Milliseconds 500 }   # SW_RESTORE: a minimized window is not on screen to be captured
    [Stage]::MoveWindow($h, 20, 20, 1422, 800, $true) | Out-Null
    $front = $false
    for ($k = 0; $k -lt 6 -and -not $front; $k++) { [Stage]::SetForegroundWindow($h) | Out-Null; Start-Sleep -Milliseconds 500; $front = ([Stage]::GetForegroundWindow() -eq $h) }
    if (-not $front) { $report = [ordered]@{ verdict = 'INCONCLUSIVE'; note = 'the app could not be brought to the front, so no frame was captured' }; throw 'INCONCLUSIVE' }
    $foregroundBefore = [Stage]::GetForegroundWindow().ToInt64()

    $ready = Join-Path $d 'benchmarks\stage-ready.txt'; $resultFile = Join-Path $d 'benchmarks\stage-result.json'
    for ($i = 0; $i -lt 240 -and -not (Test-Path $ready); $i++) { Start-Sleep -Milliseconds 250 }
    if (-not (Test-Path $ready)) { throw 'the app never signalled it was ready' }

    # Frames: the whole time from ready until the app writes its result. The baseline is the third frame taken (the app waits several seconds
    # after "ready" before its first capture, so it is a picture of the person's window at rest). Every later frame is compared with it as
    # it is taken, and only the count is kept, so a long observation does not hold hundreds of bitmaps. Only while the app is in front.
    $rows = New-Object System.Collections.Generic.List[object]; $lostForeground = $false; $base = $null; $index = 0; $blank = $false; $baseLive = $null
    while (-not (Test-Path $resultFile)) {
        if ([Stage]::GetForegroundWindow() -ne $h) { [Stage]::SetForegroundWindow($h) | Out-Null; Start-Sleep -Milliseconds 150; if ([Stage]::GetForegroundWindow() -ne $h) { $lostForeground = $true; break } }
        $rc = New-Object Stage+RECT; [void][Stage]::DwmGetWindowAttribute($h, 9, [ref]$rc, 16)
        $w = $rc.R - $rc.L; $hh = $rc.B - $rc.T
        $bm = New-Object System.Drawing.Bitmap $w, $hh, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb); $g = [System.Drawing.Graphics]::FromImage($bm)
        $t = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds(); $g.CopyFromScreen($rc.L, $rc.T, 0, 0, $bm.Size); $g.Dispose()
        $index++
        if ($index -eq 3) {
            # Refuse to judge from a picture that shows nothing: black frames compared with black frames always 'match'.
            $live = [Stage]::NonUniformFraction($bm, 360, 60, $bm.Width - 24, $bm.Height - 40)
            if ($live -lt 0.01) { $blank = $true; $baseLive = $live; $bm.Dispose(); break }
            $baseLive = $live; $base = $bm; $baseT = $t; continue
        }
        if ($SaveEveryNth -gt 0 -and ($index % $SaveEveryNth) -eq 0) { $fd = Join-Path $d 'frames'; New-Item -ItemType Directory -Force $fd | Out-Null; $bm.Save((Join-Path $fd ("f{0:D4}-{1}.png" -f $index, $t)), [System.Drawing.Imaging.ImageFormat]::Png) }
        if ($null -ne $base -and $bm.Width -eq $base.Width -and $bm.Height -eq $base.Height) {
            # the page area and toolbar, not the sidebar (its memory readout is live text)
            $rows.Add([pscustomobject]@{ t = $t; differing = [Stage]::Diff($base, $bm, 360, 60, $base.Width - 24, $base.Height - 40, 24) })
        }
        $bm.Dispose()
        if ($index -gt 1500) { break }
    }
    $foregroundAfter = [Stage]::GetForegroundWindow().ToInt64()
    for ($i = 0; $i -lt 60 -and -not (Test-Path $resultFile); $i++) { Start-Sleep -Milliseconds 250 }
    if ($blank) { $report = [ordered]@{ verdict = 'INCONCLUSIVE'; note = "the captured frames show nothing (only $([math]::Round($baseLive * 100, 2))% of the window area differs from its background): the window is minimized, off screen, covered or the screen is not capturable, so nothing can be concluded"; runDirectory = $d }; throw 'INCONCLUSIVE' }
    if ($lostForeground) { $report = [ordered]@{ verdict = 'INCONCLUSIVE'; note = 'the app lost the foreground during the observation; frames after that were not taken' }; throw 'INCONCLUSIVE' }
    $res = Get-Content $resultFile -Raw | ConvertFrom-Json
    $spans = @($res.spans)
    if ($null -eq $base -or $rows.Count -lt 20) { throw "only $($rows.Count) comparable frames were captured" }
    $first = ($spans | Measure-Object -Property beginMs -Minimum).Minimum
    if ($baseT -ge $first) { throw 'the baseline frame was not taken before the first capture' }
    $during = @(); $outside = @()
    foreach ($r in $rows) {
        $inSpan = $false; foreach ($s in $spans) { if ($r.t -ge $s.beginMs - 40 -and $r.t -le $s.endMs + 120) { $inSpan = $true; break } }
        if ($inSpan) { $during += $r } else { $outside += $r }
    }
    $worstDuring = if ($during.Count) { ($during | Measure-Object differing -Maximum).Maximum } else { $null }
    $worstOutside = if ($outside.Count) { ($outside | Measure-Object differing -Maximum).Maximum } else { $null }
    $okSpans = @($spans | Where-Object { $_.ok }).Count
    $problems = @()
    if ($okSpans -lt $spans.Count) { $problems += "$($spans.Count - $okSpans) of $($spans.Count) captures did not produce an image" }
    if ($during.Count -lt 6) { $problems += "only $($during.Count) frames fell inside a capture window: too few to say the page was never shown" }
    if ($null -ne $worstDuring -and $worstDuring -gt $TolerancePixels) { $problems += "a frame taken DURING a capture differs from the baseline in $worstDuring pixels (tolerance $TolerancePixels): the page area changed" }
    if ($foregroundBefore -ne $foregroundAfter) { $problems += 'the foreground window changed during the captures' }
    if ($res.agentPageShownAtEnd -ne 'Collapsed') { $problems += "the agent's page was left $($res.agentPageShownAtEnd), not Collapsed" }
    $verdict = if ($problems | Where-Object { $_ -notmatch 'too few frames' }) { 'FAIL' } elseif ($problems.Count) { 'INCONCLUSIVE' } else { 'PASS' }
    $report = [ordered]@{ verdict = $verdict; capturesTaken = $spans.Count; capturesThatProducedAnImage = $okSpans; baselineNonUniformFraction = [math]::Round($baseLive, 4); framesObserved = $rows.Count; framesInsideCaptureWindows = $during.Count
        worstPixelChangeDuringCaptures = $worstDuring; worstPixelChangeOutsideCaptures = $worstOutside; tolerance = $TolerancePixels
        foregroundWindowUnchanged = ($foregroundBefore -eq $foregroundAfter); agentPageShownAtEnd = $res.agentPageShownAtEnd; problems = $problems; runDirectory = $d }
    $script:exitCode = switch ($verdict) { 'PASS' { 0 } 'FAIL' { 1 } default { 2 } }
    $base.Dispose()
}
catch {
    if ($null -eq $report) { $report = [ordered]@{ verdict = 'ERROR'; error = $_.Exception.Message; runDirectory = $d }; $script:exitCode = 3 }
    elseif ($report.verdict -eq 'INCONCLUSIVE') { $script:exitCode = 2 }
}
finally {
    Stop-OwnedProcesses (Get-OwnedProcesses $proc)   # id AND start time (scripts\ProcessScope.ps1)
}
$report | ConvertTo-Json -Depth 5
exit $script:exitCode
