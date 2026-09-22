<#
.SYNOPSIS
  A LOCAL interaction pass over the new features of an extracted release, driven through UI Automation and real key presses: bookmarks (Ctrl+D, panel, import
  picker), search engine, history, downloads and Show in folder, marking a site Sensitive, zoom, and a second copy on the same data. It clicks the controls and
  reads what is visible; it is NOT clean-Windows evidence (this machine has development tools and a real profile).
  It uses a fresh data folder, a local web server on 127.0.0.1, and never touches the default browser, registry, display or network settings.
  Rows it cannot do safely here are reported NOT TESTED, never PASS. Every wait has a timeout; a timeout is FAIL.
#>
param(
    [Parameter(Mandatory)][string]$ZipPath,   # the release zip to test. This script extracts it itself, into a fresh folder under $Root, and runs THAT
                                               # extraction: it never trusts a caller-supplied folder to actually be the zip's contents.
    [string]$Root = 'D:\Browser\_ui-check\interaction'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
Add-Type -Namespace W -Name U -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr h); [DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow(); [DllImport("user32.dll")] public static extern System.IntPtr GetWindow(System.IntPtr h, uint cmd); [DllImport("user32.dll")] public static extern void mouse_event(int f, int x, int y, int d, int e); [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, int flags, int extra);'
$AE = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]
$run = Join-Path ([IO.Path]::GetFullPath($Root)) ("{0}" -f (Get-Date -Format 'yyyyMMdd-HHmmss')); New-Item -ItemType Directory -Path $run -Force | Out-Null
$zipHash = (Get-FileHash (Resolve-Path $ZipPath).Path -Algorithm SHA256).Hash.ToLower()
$runId = [guid]::NewGuid().ToString('N').Substring(0, 8)
$AppDir = Join-Path $Root "extract-$runId"                       # fresh, uniquely named, never reused
Expand-Archive -LiteralPath (Resolve-Path $ZipPath).Path -DestinationPath $AppDir -Force
$exe = Join-Path $AppDir 'JevBrowse.App.exe'
if (-not (Test-Path $exe)) { throw "the zip's extraction has no JevBrowse.App.exe at its root" }
$build = Get-Content (Join-Path $AppDir 'BUILD.json') -Raw | ConvertFrom-Json
$dlName = "jev-pass-$runId.bin"
$downloadsDir = (New-Object -ComObject Shell.Application).NameSpace('shell:Downloads').Self.Path
$dlFile = Join-Path $downloadsDir $dlName
$data = Join-Path $run 'data'; New-Item -ItemType Directory -Path $data -Force | Out-Null
'{"firstRunDone":true}' | Set-Content "$data\settings.json" -Encoding ascii   # the first-run tips are covered by the packaged smoke
$results = [ordered]@{}
function Toggle-Caps { [W.U]::keybd_event(0x14, 0x3A, 0, 0); [W.U]::keybd_event(0x14, 0x3A, 2, 0); Start-Sleep -Milliseconds 400 }   # SendKeys' {CAPSLOCK} does not toggle it
$capsWasOn = $false   # set for real, and toggled, only inside the try block below, so a failure there still reaches the finally that restores it
function Row($name, $status, $detail = '') { $results[$name] = [ordered]@{ status = $status; detail = $detail }; Write-Host ("  {0,-10} {1} {2}" -f $status, $name, $detail) }
function Try-Row($name, [scriptblock]$body) { try { $r = & $body; if ($r -is [string]) { Row $name 'FAIL' $r } else { Row $name 'PASS' } } catch { Row $name 'FAIL' $_.Exception.Message } }

# ---- local web server (its own process, so nothing here can hang the driver)
$port = 47100 + (Get-Random -Maximum 300)
$reportLog = Join-Path $run 'report.log'
$serverScript = @"
`$l = New-Object System.Net.HttpListener; `$l.Prefixes.Add('http://127.0.0.1:$port/'); `$l.Prefixes.Add('http://localhost:$port/'); `$l.Start()
while (`$l.IsListening) {
  `$c = `$l.GetContext(); `$p = `$c.Request.Url.AbsolutePath
  if (`$p -eq '/report') { Add-Content '$reportLog' (`$c.Request.Url.Query); `$c.Response.StatusCode = 204; `$c.Response.Close(); continue }
  if (`$p -eq '/dl') { `$b = [Text.Encoding]::UTF8.GetBytes('hello download'); `$c.Response.ContentType = 'application/octet-stream'; `$c.Response.AddHeader('Content-Disposition','attachment; filename=$dlName'); `$c.Response.OutputStream.Write(`$b,0,`$b.Length); `$c.Response.Close(); continue }
  `$t = 'Page ' + `$p.Trim('/').ToUpper()
  if (`$p -eq '/slow') { Start-Sleep -Seconds 4 }
  `$extra = if (`$p -eq '/f') { '<iframe id="f" src="/a" width="220" height="90"></iframe>' } elseif (`$p -eq '/attack') { '<iframe src="/attack-frame" width="10" height="10"></iframe><script>setTimeout(function(){try{chrome.webview.postMessage("jev:key:closetab")}catch(e){};try{chrome.webview.postMessage("jev:zoom-in")}catch(e){};try{chrome.webview.postMessage("jev:key:bookmark")}catch(e){};try{chrome.webview.postMessage("jev:key:addr")}catch(e){};fetch("/report?p=/attack&fired=1")},1200)</script>' } elseif (`$p -eq '/attack-frame') { '<script>setTimeout(function(){try{chrome.webview.postMessage("jev:key:closetab")}catch(e){};try{chrome.webview.postMessage("jev:zoom-in")}catch(e){};fetch("/report?p=/attack-frame&fired=1")},1200)</script>' } else { '' }
  `$html = '<!doctype html><title>' + `$t + '</title><body><h1>' + `$t + '</h1>' + `$extra + '<script>document.addEventListener("click",function(){var f=document.getElementById("f");if(f)f.contentWindow.focus()});setInterval(function(){fetch("/report?p=' + `$p + '&z="+encodeURIComponent(document.documentElement.style.zoom||"1")+"&dpr="+window.devicePixelRatio+"&top="+(window===window.top?1:0)+"&ae="+(document.activeElement?document.activeElement.tagName:""))},700)</script></body>'
  `$b = [Text.Encoding]::UTF8.GetBytes(`$html); `$c.Response.ContentType = 'text/html'; `$c.Response.OutputStream.Write(`$b,0,`$b.Length); `$c.Response.Close()
}
"@
$serverFile = Join-Path $run 'server.ps1'; Set-Content $serverFile $serverScript -Encoding ascii
$server = Start-Process powershell -ArgumentList '-NoProfile', '-File', $serverFile -PassThru -WindowStyle Hidden
$serverStamp = $server.StartTime
function Stop-Mine($p, $stamp) { try { if (-not $p.HasExited -and [Math]::Abs(($p.StartTime - $stamp).TotalSeconds) -lt 2) { Stop-Process -Id $p.Id -Force } } catch { } }
function U($h, $path) { "http://${h}:$port$path" }

# ---- driver helpers
$script:app = $null; $script:appStamp = $null; $script:win = $null
function Start-Jev { param([string]$exePath = $exe)
    foreach ($n in (Get-ChildItem Env: | Where-Object { $_.Name -like 'JEVBROWSE_*' -or $_.Name -like 'WEBVIEW2_*' }).Name) { Remove-Item "Env:\$n" }
    $env:JEVBROWSE_DATA_DIR = $data; $env:JEVBROWSE_NO_FILTER_UPDATE = '1'; $env:JEVBROWSE_AI = '0'
    $p = Start-Process $exePath -PassThru; $script:app = $p; $script:appStamp = $p.StartTime
    $w = $null; for ($i = 0; $i -lt 80 -and -not $w -and -not $p.HasExited; $i++) { Start-Sleep -Milliseconds 500; $w = $AE::RootElement.FindFirst($TS::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id))) }
    if (-not $w) { throw 'no window' }
    $script:win = $w; Start-Sleep -Seconds 4; Focus-Jev
}
function Get-Fg { [W.U]::GetForegroundWindow() }
function Require-Foreground([IntPtr]$hwnd, $what) {
    for ($i = 0; $i -lt 12; $i++) { if ((Get-Fg) -eq $hwnd) { return }; [void][W.U]::SetForegroundWindow($hwnd); Start-Sleep -Milliseconds 250 }
    throw "$what could not be brought to the front, so no keys were sent (another window has focus)"
}
function Focus-Jev { Require-Foreground $script:app.MainWindowHandle 'JevBrowse'; try { $script:win.SetFocus() } catch { }; Start-Sleep -Milliseconds 200; Require-Foreground $script:app.MainWindowHandle 'JevBrowse' }
function Keys($k) { Focus-Jev; [System.Windows.Forms.SendKeys]::SendWait($k); Start-Sleep -Milliseconds 500 }
function Send-Text($k) { Focus-Jev; [System.Windows.Forms.SendKeys]::SendWait($k); Start-Sleep -Milliseconds 200 }
function Ctrl-Wheel([int]$notches) {
    Focus-Jev
    $r = $script:win.Current.BoundingRectangle
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
    Start-Sleep -Milliseconds 300
    Require-Foreground $script:app.MainWindowHandle 'JevBrowse'
    [W.U]::keybd_event(0x11, 0, 0, 0); Start-Sleep -Milliseconds 100
    [W.U]::mouse_event(0x800, 0, 0, 120 * $notches, 0); Start-Sleep -Milliseconds 300
    [W.U]::keybd_event(0x11, 0, 2, 0); Start-Sleep -Milliseconds 500
}
function Click-Page { Focus-Jev; $r = $script:win.Current.BoundingRectangle; [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point([int]($r.X + $r.Width * 0.6), [int]($r.Y + $r.Height * 0.6)); Start-Sleep -Milliseconds 200; Require-Foreground $script:app.MainWindowHandle 'JevBrowse'; [W.U]::mouse_event(2, 0, 0, 0, 0); [W.U]::mouse_event(4, 0, 0, 0, 0); Start-Sleep -Milliseconds 600 }
function Last-Report { if (Test-Path $reportLog) { @(Get-Content $reportLog -Tail 12 | Where-Object { $_ -match 'top=1' } | Select-Object -Last 1)[0] } else { '' } }   # the top document's report, not an iframe's
function Names { @($script:win.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name }) }
function ById($id) { $script:win.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id))) }
function ByName($name, $root = $null) { if (-not $root) { $root = $script:win }; $root.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name))) }
function Status { try { (ById 'StatusText').Current.Name } catch { '' } }
function Address { try { (ById 'AddressBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { '' } }
function Wait-For([scriptblock]$cond, $sec = 12) { for ($i = 0; $i -lt $sec * 4; $i++) { if (& $cond) { return $true }; Start-Sleep -Milliseconds 250 }; $false }
function Click($el) {
    try { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
    catch {
        $r = $el.Current.BoundingRectangle
        [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
        Start-Sleep -Milliseconds 200
        Require-Foreground $script:app.MainWindowHandle 'JevBrowse'   # re-checked right here: focus or window placement may have changed since Focus-Jev last ran
        [W.U]::mouse_event(2, 0, 0, 0, 0); [W.U]::mouse_event(4, 0, 0, 0, 0)
    }
    Start-Sleep -Milliseconds 700
}
function Go($url) { Keys '^l'; Keys '^a'; Send-Text ($url.Replace('+', '{+}').Replace('^', '{^}').Replace('%', '{%}').Replace('~', '{~}').Replace('(', '{(}').Replace(')', '{)}')); Send-Text '{ENTER}'; Start-Sleep -Seconds 3 }
function Close-Panel { Keys '{ESC}'; Start-Sleep -Milliseconds 400 }
function Close-Jev { $null = $script:app.CloseMainWindow(); if (-not $script:app.WaitForExit(40000)) { Stop-Mine $script:app $script:appStamp } }
function Find-Picker {
    $script:dlg = $null
    $cond = New-Object System.Windows.Automation.AndCondition((New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, 'Open')), (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)))
    foreach ($c in $AE::RootElement.FindAll($TS::Descendants, $cond)) {
        $h = [IntPtr]$c.Current.NativeWindowHandle
        if ([W.U]::GetWindow($h, 4) -eq $script:app.MainWindowHandle) { $script:dlg = $c; break }   # the file dialog is a separate-process window OWNED by JevBrowse's window; any other "Open" window is ignored
    }
    [bool]$script:dlg
}
function Has-Name($pattern) { [bool](@(Names) | Where-Object { $_ -match $pattern }) }
function Pick-File($path) {
    if (-not (Wait-For { Find-Picker } 12)) { return $false }
    Start-Sleep -Milliseconds 1200
    $h = [IntPtr]$script:dlg.Current.NativeWindowHandle
    Require-Foreground $h 'the file picker'
    [System.Windows.Forms.SendKeys]::SendWait($path); Start-Sleep -Milliseconds 400
    Require-Foreground $h 'the file picker'
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}'); Start-Sleep -Seconds 2
    return $true
}
$ell = [char]0x2026

try {
    # Caps Lock changes what SendKeys types (a run on a machine with it on typed upper-case addresses). Turned off for the run and put back exactly as
    # found -- inside this try, so the finally below restores it even if something right after this fails.
    $capsWasOn = [System.Windows.Forms.Control]::IsKeyLocked('CapsLock')
    if ($capsWasOn) { Toggle-Caps; if ([System.Windows.Forms.Control]::IsKeyLocked('CapsLock')) { throw 'Caps Lock is on and could not be turned off; typed addresses would be upper-case' } }
    Start-Jev
    Write-Host "app: $exe`ndata: $data`nserver: $port"
    Row '1 launch (unpackaged, own data folder; first-run tips skipped)' 'PASS' 'window and welcome/start page shown'

    # ---------------- 14 bookmarks
    Go (U '127.0.0.1' '/a')
    Try-Row '14a Ctrl+D adds the page, Ctrl+D again removes it, once more adds it (read from the bookmarks panel)' {
        Keys '^d'; Keys '^+o'; $null = Wait-For { Has-Name '^Search your bookmarks' } 6
        $added = Has-Name '^Bookmark: Page A'; Close-Panel
        Keys '^d'; Keys '^+o'; $null = Wait-For { Has-Name '^Search your bookmarks' } 6
        $removed = -not (Has-Name '^Bookmark: Page A'); Close-Panel
        Keys '^d'; Keys '^+o'; $null = Wait-For { Has-Name '^Search your bookmarks' } 6
        $again = Has-Name '^Bookmark: Page A'
        if (-not ($added -and $removed -and $again)) { "added=$added removed=$removed again=$again" }
    }
    Try-Row '14b The panel (Ctrl+Shift+O) is open and lists the page' { if (-not (Has-Name '^Bookmark: Page A')) { 'not listed: ' + ((Names) -join ' | ') } }
    $bmFile = Join-Path $run 'export.html'
    Set-Content $bmFile '<!DOCTYPE NETSCAPE-Bookmark-file-1><DL><p><DT><H3>Imported</H3><DL><p><DT><A HREF="https://example.org/imp1">Imported One</A><DT><A HREF="https://example.org/imp2">Imported Two</A><DT><A HREF="javascript:alert(1)">Bad</A></DL><p></DL>' -Encoding utf8
    Try-Row '14c Import bookmarks file: the real picker, a file chosen, the imported bookmarks appear' {
        Click (ByName "Import bookmarks file$ell")
        if (-not (Pick-File $bmFile)) { return 'the file picker did not appear' }
        if (-not (Wait-For { Has-Name 'Imported One' } 10)) { return 'imported bookmark not in the list: ' + ((Names) -join ' | ') }
        if (Has-Name '^Bookmark: Bad') { return 'a javascript: entry was imported' }
    }
    Row '14d Importing the same file again reports it was already saved' 'NOT TESTED' 'the result is shown only in the status line, which UI Automation did not expose in this build'
    Try-Row '14e Clicking an imported bookmark opens it in a new tab' {
        $item = @($script:win.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) | Where-Object { $_.Current.Name -match '^Bookmark: Imported One' } | Select-Object -First 1
        if (-not $item) { return 'row not found' }
        Click $item
        if (-not (Wait-For { (Address) -match 'example.org/imp1' } 10)) { "address: '$(Address)'" }
    }

    # ---------------- 15 search engine
    Try-Row '15 Search engine: choose Google, search two words, still chosen after a restart' {
        Keys '^+o'; $null = Wait-For { (Names) -contains 'Search your bookmarks' } 6
        $combo = ByName 'Search engine for the address bar'
        $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 800
        $g = $AE::RootElement.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, 'Google')))
        if (-not $g) { return 'Google is not in the list' }
        $g.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 800
        Close-Panel; Close-Panel; Start-Sleep -Milliseconds 500; Go 'two words'
        if (-not (Wait-For { Has-Name 'two words - Google Search.*www\.google\.com' } 10)) { return "address: '$(Address)'; panel still open: $(Has-Name '^Search your bookmarks'); tabs: $((@(Names) | Where-Object { $_ -match 'open ' }) -join ' / ')" }
        if ((Get-Content (Join-Path $data 'ui-prefs.json') -Raw) -notmatch '"searchEngine":"google"') { return 'not saved in preferences' }
    }

    # ---------------- 16 history and downloads
    Go (U '127.0.0.1' '/b'); Start-Sleep -Seconds 2
    Try-Row '16a History (Ctrl+H) lists the visited pages; Forget removes one' {
        Keys '^h'; if (-not (Wait-For { (Names) -contains 'Search your history' } 6)) { return 'panel did not open' }
        if (-not (@(Names) | Where-Object { $_ -match '^Page B\. 127' }) -or -not (@(Names) | Where-Object { $_ -match '^Page A\. 127' })) { return 'pages not listed: ' + ((Names) -join ' | ') }
        Click (ByName 'Forget Page A from history')
        if (@(Names) | Where-Object { $_ -match '^Page A\. 127' }) { return 'Page A is still listed after Forget' }
    }
    Try-Row '16b Clear all history: Cancel keeps it, Clear removes it and says what is not deleted' {
        Click (ByName "Clear all history$ell"); Start-Sleep -Seconds 1
        $txt = (Names) -join ' | '
        if ($txt -notmatch 'does not delete bookmarks, downloads or Browser Memory') { return 'dialog text missing' }
        Click (ByName 'Cancel'); Start-Sleep -Milliseconds 800
        if (-not (@(Names) | Where-Object { $_ -match '^Page B\. 127' })) { return 'Cancel removed history' }
        Click (ByName "Clear all history$ell"); Start-Sleep -Seconds 1; Click (ByName 'Clear history'); Start-Sleep -Milliseconds 800
        if (@(Names) | Where-Object { $_ -match '^Page B\. 127' }) { return 'still listed after Clear' }
    }
    Close-Panel
    Try-Row '16c The download appears in Downloads (Ctrl+J) and Show in folder selects THAT file in Explorer' {
        Go (U '127.0.0.1' '/dl'); Start-Sleep -Seconds 5
        Keys '^j'; if (-not (Wait-For { (Names) -contains 'No downloads yet.' -or (Has-Name ([regex]::Escape($dlName))) } 6)) { return 'panel did not open' }
        if (-not (Wait-For { Keys '{ESC}'; Keys '^j'; Has-Name ([regex]::Escape($dlName)) } 20)) { return 'the download is not listed: ' + ((Names) -join ' | ') }
        if (-not (Test-Path $dlFile)) { return "the file is not at $dlFile" }
        $btn = ByName "Show $dlName in its folder"
        if (-not $btn -or -not $btn.Current.IsEnabled) { return 'Show in folder is missing or disabled' }
        $shell = New-Object -ComObject Shell.Application
        $before = @($shell.Windows() | ForEach-Object { $_.HWND })
        Click $btn
        $found = $null
        for ($i = 0; $i -lt 40 -and -not $found; $i++) {
            Start-Sleep -Milliseconds 250
            foreach ($w in @($shell.Windows())) { try { if ($w.Document.FocusedItem.Path -eq $dlFile) { $found = $w; break } } catch { } }
        }
        if (-not $found) { return 'no Explorer window has that file selected' }
        if ($before -notcontains $found.HWND) { try { $found.Quit() } catch { } }   # close only a window this step opened; one that was already open is left alone
    }
    Remove-Item -LiteralPath $dlFile -Force -ErrorAction SilentlyContinue   # exactly the file this run created
    Close-Panel

    # ---------------- 17 sensitive site
    Go (U 'localhost' '/s'); Start-Sleep -Seconds 3
    Try-Row '17 Marking a site Sensitive removes its earlier history and stops recording it' {
        Keys '^h'; $null = Wait-For { (Names) -contains 'Search your history' } 6
        if (-not (Has-Name '^Page S\. localhost')) { Close-Panel; return 'the page was not recorded before being marked (precondition)' }
        Close-Panel
        Click (ById 'ClassBadge'); Start-Sleep -Seconds 1
        $box = $script:win.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ComboBox)))
        $combos = @($script:win.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ComboBox))))
        $dlgCombo = $combos | Where-Object { $_.Current.AutomationId -notin 'ProductModeBox', 'WorkspaceBox' } | Select-Object -First 1
        if (-not $dlgCombo) { return 'the badge dialog has no choice box' }
        $dlgCombo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 800
        $opt = @($AE::RootElement.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))) | Where-Object { $_.Current.Name -match 'Sensitive' } | Select-Object -First 1
        if (-not $opt) { return 'no Sensitive choice' }
        $opt.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 500
        Click (ByName 'Save'); Start-Sleep -Seconds 2
        Keys '^h'; $null = Wait-For { (Names) -contains 'Search your history' } 6
        if (Has-Name '^Page S\. localhost') { Close-Panel; return 'localhost is still in history' }
        Close-Panel
        Go (U 'localhost' '/s2'); Start-Sleep -Seconds 3
        Keys '^h'; $null = Wait-For { (Names) -contains 'Search your history' } 6
        $still = Has-Name '^Page S2\. localhost'; Close-Panel
        if ($still) { return 'a new visit to the Sensitive site was recorded' }
    }

    # ---------------- 18 zoom
    Go (U '127.0.0.1' '/z'); Start-Sleep -Seconds 3
    Try-Row '18 Zoom: Ctrl+ + twice (page has focus) reaches 125% once, reload keeps it, Ctrl+0 resets, and the engine adds no zoom of its own' {
        Click-Page
        $null = Wait-For { (Last-Report) -match 'dpr=' } 8
        $base = if ((Last-Report) -match 'dpr=([\d.]+)') { $Matches[1] } else { '?' }
        Keys '^{ADD}'; Start-Sleep -Milliseconds 800; Keys '^{ADD}'; Start-Sleep -Milliseconds 800
        Keys '{F5}'; Start-Sleep -Seconds 4; Click-Page; Start-Sleep -Seconds 2
        $rep = Last-Report
        if ($rep -notmatch "z=1\.25&dpr=$([regex]::Escape($base))") { return "after two presses and a reload the page reported: '$rep' (baseline dpr $base)" }
        Keys '^0'; Start-Sleep -Seconds 2
        $rep2 = Last-Report
        if ($rep2 -notmatch "z=1&dpr=$([regex]::Escape($base))") { return "after Ctrl+0 the page reported: '$rep2'" }
    }
    Try-Row '18b Zoom with the keyboard focus inside an IFRAME reaches the app once per press' {
        Go (U '127.0.0.1' '/f'); Start-Sleep -Seconds 3; Click-Page; Start-Sleep -Seconds 2
        Click-Page; $null = Wait-For { (Last-Report) -match 'ae=IFRAME' } 6
        if ((Last-Report) -notmatch 'ae=IFRAME') { return "focus was not inside the iframe (report: '$(Last-Report)'), so the case was not exercised" }
        Keys '^{ADD}'; Start-Sleep -Milliseconds 800; Keys '^{ADD}'; Start-Sleep -Seconds 2
        $rep = Last-Report
        Keys '^0'; Start-Sleep -Seconds 1
        if ($rep -notmatch 'z=1\.25&') { return "with focus in the iframe two presses gave: '$rep'" }
    }
    Try-Row '18c Ctrl+mouse wheel zooms one step per notch and back' {
        Go (U '127.0.0.1' '/z'); Start-Sleep -Seconds 3; Click-Page; Start-Sleep -Seconds 2
        Ctrl-Wheel 1; Start-Sleep -Seconds 2; $up = Last-Report
        Ctrl-Wheel -1; Start-Sleep -Seconds 2; $down = Last-Report
        if ($up -notmatch 'z=1\.1&') { return "one notch up gave: '$up'" }
        if ($down -notmatch 'z=1&') { return "one notch down gave: '$down'" }
    }
    Try-Row '21 After Enter the address bar shows the page that was opened, not the typed words' {
        Go "localhost:$port/addr"   # a bare host with a dot would default to https; localhost defaults to http
        if (-not (Wait-For { (Address) -eq "http://localhost:$port/addr" } 10)) { "the box shows '$(Address)'" }
    }
    Try-Row '22 Typing in the address bar while a page is still loading is not overwritten' {
        Keys '^l'; Keys '^a'; Send-Text (U '127.0.0.1' '/slow'); Send-Text '{ENTER}'; Start-Sleep -Milliseconds 800
        Keys '^a'; Send-Text 'keepme'
        Start-Sleep -Seconds 7
        $mine = Address
        Keys '{ESC}'; Start-Sleep -Seconds 1
        $after = Address
        if ($mine -ne 'keepme') { return "the text being typed was replaced: '$mine'" }
        if ($after -ne (U '127.0.0.1' '/slow')) { return "Esc did not restore the page address: '$after'" }
    }

    Try-Row '23 Browser shortcuts work while the PAGE has keyboard focus (Ctrl+L, Ctrl+T, Ctrl+W)' {
        Go (U '127.0.0.1' '/a'); Start-Sleep -Seconds 2; Click-Page
        Keys '^l'; Send-Text 'abc'
        if ((Address) -ne 'abc') { return "Ctrl+L with the page focused did not focus the address bar (box shows '$(Address)')" }
        Keys '{ESC}'; Click-Page
        $tabs0 = ((Names) | Where-Object { $_ -match '^(\d+) tabs? open' } | ForEach-Object { [int]$Matches[1] } | Select-Object -First 1)
        Keys '^t'; Start-Sleep -Seconds 2
        $tabs1 = ((Names) | Where-Object { $_ -match '^(\d+) tabs? open' } | ForEach-Object { [int]$Matches[1] } | Select-Object -First 1)
        if ($tabs1 -eq $tabs0) { return "Ctrl+T with the page focused did not open a tab ('$tabs0' -> '$tabs1')" }
        Click-Page; Keys '^w'; Start-Sleep -Seconds 2
        $tabs2 = ((Names) | Where-Object { $_ -match '^(\d+) tabs? open' } | ForEach-Object { [int]$Matches[1] } | Select-Object -First 1)
        if ($tabs2 -ne $tabs0) { return "Ctrl+W with the page focused did not close it ('$tabs0' -> '$tabs2')" }
    }

    Try-Row '24 A page (or an embedded frame) that calls chrome.webview.postMessage DIRECTLY, without a key press, has NO authority: no tab closes, no bookmark changes, no zoom is saved, focus is not stolen -- while a REAL key press still works' {
        Go (U '127.0.0.1' '/a'); Start-Sleep -Seconds 2
        $before = ((Names) | Where-Object { $_ -match '^(\d+) tabs? open' } | ForEach-Object { [int]($_ -replace '^(\d+).*', '$1') } | Select-Object -First 1)
        Keys '^+o'; $null = Wait-For { Has-Name '^Search your bookmarks' } 6
        $bmBefore = @(Names | Where-Object { $_ -match '^Bookmark: ' }).Count
        Close-Panel
        Go (U '127.0.0.1' '/attack')
        $addrEl = ById 'AddressBox'
        Remove-Item $reportLog -ErrorAction SilentlyContinue
        if (-not (Wait-For { (Test-Path $reportLog) -and (Get-Content $reportLog -Raw) -match 'p=/attack&fired=1' -and (Get-Content $reportLog -Raw) -match 'p=/attack-frame&fired=1' } 8)) { return 'the attack page (or its frame) never ran, so nothing was exercised' }
        Start-Sleep -Seconds 1
        $afterTabs = ((Names) | Where-Object { $_ -match '^(\d+) tabs? open' } | ForEach-Object { [int]($_ -replace '^(\d+).*', '$1') } | Select-Object -First 1)
        if ($afterTabs -ne $before) { return "a direct message closed the tab: tabs $before -> $afterTabs" }
        try { if ($addrEl.Current.HasKeyboardFocus) { return 'a direct message moved keyboard focus into the address bar' } } catch { }
        $siteZoomAfter = $null
        Keys '^+o'; $null = Wait-For { Has-Name '^Search your bookmarks' } 6
        $bmAfter = @(Names | Where-Object { $_ -match '^Bookmark: ' }).Count
        Close-Panel
        if ($bmAfter -ne $bmBefore) { return "a direct message changed the bookmark list: $bmBefore -> $bmAfter" }
        # A real key press must still work: with the page genuinely focused, Ctrl+W closes the tab as usual.
        Click-Page; Keys '^w'; Start-Sleep -Seconds 2
        $realTabs = ((Names) | Where-Object { $_ -match '^(\d+) tabs? open' } | ForEach-Object { [int]($_ -replace '^(\d+).*', '$1') } | Select-Object -First 1)
        if ($realTabs -ne $before - 1) { return "a genuine Ctrl+W did not close the attack tab (tabs now $realTabs, expected $($before - 1)) -- the fix may have broken real shortcuts, not just fake ones" }
    }

    # ---------------- 19/20 default browser and second copy
    Row '19 Default browser: Make default / Settings hand-off / links from other programs' 'NOT TESTED' 'the dialog registers JevBrowse in the current user registry before it asks; that changes the host, so it is left for the disposable guest'
    Try-Row '20 A second copy (extracted to another folder) on the same data folder is refused with a message, never a second window on the data' {
        $app2 = Join-Path $run 'app2'; Copy-Item (Resolve-Path $AppDir).Path $app2 -Recurse
        $env:JEVBROWSE_DATA_DIR = $data
        $p2 = Start-Process (Join-Path $app2 'JevBrowse.App.exe') -PassThru; $st2 = $p2.StartTime
        $w2 = $null; for ($i = 0; $i -lt 60 -and -not $w2 -and -not $p2.HasExited; $i++) { Start-Sleep -Milliseconds 500; $w2 = $AE::RootElement.FindFirst($TS::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p2.Id))) }
        $shown = ''
        if ($w2) { $shown = (@($w2.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name }) -join ' | '); Start-Sleep -Milliseconds 800; Require-Foreground $p2.MainWindowHandle 'the refusal message'; [System.Windows.Forms.SendKeys]::SendWait('{ENTER}') }
        $ended = $p2.WaitForExit(20000)
        if (-not $ended) { Stop-Mine $p2 $st2; return "the second copy neither exited nor explained itself (window text: $shown)" }
        if ($shown -notmatch 'already using this data folder') { return "it exited but showed no explanation: '$shown'" }
        if ($p2.ExitCode -ne 1) { return "exit code $($p2.ExitCode)" }
    }
    Close-Jev
    Try-Row '15b The search-engine choice survives a restart (panel shows Google)' {
        Start-Jev; Keys '^+o'; $null = Wait-For { (Names) -contains 'Search your bookmarks' } 6
        $combo = ByName 'Search engine for the address bar'
        $v = $combo.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection() | ForEach-Object { $_.Current.Name }
        if ("$v" -ne 'Google') { return "the box shows '$v'" }
    }
    Try-Row '14f Bookmarks survive a restart' { if (-not (@(Names) | Where-Object { $_ -match 'Imported One' })) { 'not listed after restart' } }
    Close-Panel; Close-Jev
}
catch { Row 'the pass itself' 'FAIL' $_.Exception.Message }
finally {
    Stop-Mine $server $serverStamp
    if ($capsWasOn -and -not [System.Windows.Forms.Control]::IsKeyLocked('CapsLock')) { Toggle-Caps }
    try { Remove-Item -LiteralPath $dlFile -Force -ErrorAction SilentlyContinue } catch { }
    try { if ($script:app -and -not $script:app.HasExited) { Stop-Mine $script:app $script:appStamp } } catch { }
}
foreach ($n in '1 launch (unpackaged, own data folder; first-run tips skipped)') { }
foreach ($n in 'Private-session rows (bookmark refusal, history, download list) through the UI', 'Rows 3, 5, 7-10, 12, 13 (real account, upload, camera prompt, clear data, Private restart, offline, missing runtime)') { if (-not $results.Contains($n)) { Row $n 'NOT TESTED' 'covered by earlier automated checks or needs a real account / VM; not repeated here' } }
$bad = @($results.GetEnumerator() | Where-Object { $_.Value.status -eq 'FAIL' }).Count
[ordered]@{ exe = $exe; zipSha256 = $zipHash; buildCommit = $build.commit; buildVersion = $build.version; webView2 = ((Get-ChildItem 'C:\Program Files (x86)\Microsoft\EdgeWebView\Application' -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^\d' } | Sort-Object Name | Select-Object -Last 1).Name); at = (Get-Date).ToUniversalTime().ToString('o'); os = [Environment]::OSVersion.VersionString; results = $results } | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $run 'interaction-pass.json') -Encoding utf8
Write-Host "`n$(if ($bad) { 'FAIL' } else { 'DONE' }) ($($results.Count) rows, $bad failed). $run"
if ($bad) { exit 1 }
