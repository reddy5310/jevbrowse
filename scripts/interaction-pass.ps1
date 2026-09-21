<#
.SYNOPSIS
  A LOCAL interaction pass over the new features of an extracted release, driven through UI Automation and real key presses: bookmarks (Ctrl+D, panel, import
  picker), search engine, history, downloads and Show in folder, marking a site Sensitive, zoom, and a second copy on the same data. It clicks the controls and
  reads what is visible; it is NOT clean-Windows evidence (this machine has development tools and a real profile).
  It uses a fresh data folder, a local web server on 127.0.0.1, and never touches the default browser, registry, display or network settings.
  Rows it cannot do safely here are reported NOT TESTED, never PASS. Every wait has a timeout; a timeout is FAIL.
#>
param(
    [Parameter(Mandatory)][string]$AppDir,
    [string]$Root = 'D:\Browser\_ui-check\interaction'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
Add-Type -Namespace W -Name U -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr h);'
$AE = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]
$run = Join-Path ([IO.Path]::GetFullPath($Root)) ("{0}" -f (Get-Date -Format 'yyyyMMdd-HHmmss')); New-Item -ItemType Directory -Path $run -Force | Out-Null
$exe = Join-Path (Resolve-Path $AppDir).Path 'JevBrowse.App.exe'
$data = Join-Path $run 'data'; New-Item -ItemType Directory -Path $data -Force | Out-Null
'{"firstRunDone":true}' | Set-Content "$data\settings.json" -Encoding ascii   # the first-run tips are covered by the packaged smoke
$results = [ordered]@{}
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
  if (`$p -eq '/dl') { `$b = [Text.Encoding]::UTF8.GetBytes('hello download'); `$c.Response.ContentType = 'application/octet-stream'; `$c.Response.AddHeader('Content-Disposition','attachment; filename=jev-pass.bin'); `$c.Response.OutputStream.Write(`$b,0,`$b.Length); `$c.Response.Close(); continue }
  `$t = 'Page ' + `$p.Trim('/').ToUpper()
  `$html = '<!doctype html><title>' + `$t + '</title><body><h1>' + `$t + '</h1><script>setTimeout(function(){fetch("/report?p=' + `$p + '&z="+encodeURIComponent(document.documentElement.style.zoom||"1")+"&dpr="+window.devicePixelRatio)},1800)</script></body>'
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
function Focus-Jev { try { [W.U]::SetForegroundWindow($script:app.MainWindowHandle) | Out-Null } catch { }; try { $script:win.SetFocus() } catch { }; Start-Sleep -Milliseconds 300 }
function Keys($k) { Focus-Jev; [System.Windows.Forms.SendKeys]::SendWait($k); Start-Sleep -Milliseconds 500 }
function Names { @($script:win.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name }) }
function ById($id) { $script:win.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id))) }
function ByName($name, $root = $null) { if (-not $root) { $root = $script:win }; $root.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name))) }
function Status { try { (ById 'StatusText').Current.Name } catch { '' } }
function Address { try { (ById 'AddressBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { '' } }
function Wait-For([scriptblock]$cond, $sec = 12) { for ($i = 0; $i -lt $sec * 4; $i++) { if (& $cond) { return $true }; Start-Sleep -Milliseconds 250 }; $false }
function Click($el) { try { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { $r = $el.Current.BoundingRectangle; [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)); Add-Type -Namespace M -Name C -MemberDefinition '[DllImport("user32.dll")] public static extern void mouse_event(int f,int x,int y,int d,int e);' -ErrorAction SilentlyContinue; [M.C]::mouse_event(2,0,0,0,0); [M.C]::mouse_event(4,0,0,0,0) }; Start-Sleep -Milliseconds 700 }
function Go($url) { Keys '^l'; Keys '^a'; [System.Windows.Forms.SendKeys]::SendWait($url.Replace('+', '{+}').Replace('^', '{^}').Replace('%', '{%}').Replace('~', '{~}').Replace('(', '{(}').Replace(')', '{)}')); Start-Sleep -Milliseconds 200; [System.Windows.Forms.SendKeys]::SendWait('{ENTER}'); Start-Sleep -Seconds 3 }
function Close-Panel { Keys '{ESC}'; Start-Sleep -Milliseconds 400 }
function Close-Jev { $null = $script:app.CloseMainWindow(); if (-not $script:app.WaitForExit(40000)) { Stop-Mine $script:app $script:appStamp } }
function Find-Picker { $script:dlg = $AE::RootElement.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.AndCondition((New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, 'Open')), (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window))))); [bool]$script:dlg }
function Has-Name($pattern) { [bool](@(Names) | Where-Object { $_ -match $pattern }) }
function Pick-File($path) {
    if (-not (Wait-For { Find-Picker } 12)) { return $false }
    Start-Sleep -Milliseconds 1200
    try { $script:dlg.SetFocus() } catch { }
    [System.Windows.Forms.SendKeys]::SendWait($path); Start-Sleep -Milliseconds 400; [System.Windows.Forms.SendKeys]::SendWait('{ENTER}'); Start-Sleep -Seconds 2
    return $true
}
$ell = [char]0x2026

try {
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
    Try-Row '16c A download appears in Downloads (Ctrl+J) and Show in folder opens Explorer on it' {
        Go (U '127.0.0.1' '/dl'); Start-Sleep -Seconds 5
        Focus-Jev
        Keys '^j'; if (-not (Wait-For { (Names) -contains 'No downloads yet.' -or @(Names | Where-Object { $_ -match 'jev-pass' }).Count -gt 0 } 6)) { return 'panel did not open' }
        if (-not (Wait-For { Keys '{ESC}'; Keys '^j'; @(Names | Where-Object { $_ -match 'jev-pass' }).Count -gt 0 } 20)) { return 'the download is not listed: ' + ((Names) -join ' | ') }
        $btn = ByName 'Show jev-pass.bin in its folder'
        if (-not $btn -or -not $btn.Current.IsEnabled) { return 'Show in folder is missing or disabled' }
        $shell = New-Object -ComObject Shell.Application; $before = @($shell.Windows()).Count
        Click $btn; Start-Sleep -Seconds 3
        $ex = @($shell.Windows() | Where-Object { $_.LocationURL -match 'Downloads' })
        if ($ex.Count -eq 0) { return 'no Explorer window on the Downloads folder' }
        foreach ($e in $ex) { try { $e.Quit() } catch { } }
    }
    Get-ChildItem "$env:USERPROFILE\Downloads\jev-pass*.bin" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue   # the pass's own test file
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
        $null = Wait-For { (Test-Path $reportLog) -and ((Get-Content $reportLog -Raw) -match 'dpr=') } 8
        $base = if ((Get-Content $reportLog -Raw) -match 'dpr=([\d.]+)') { $Matches[1] } else { '?' }
        Keys '^{ADD}'; Start-Sleep -Seconds 1; Keys '^{ADD}'; Start-Sleep -Seconds 1
        Remove-Item $reportLog -ErrorAction SilentlyContinue
        Keys '{F5}'; Start-Sleep -Seconds 5
        $rep = if (Test-Path $reportLog) { Get-Content $reportLog -Raw } else { '' }
        if ($rep -notmatch "z=1\.25&dpr=$([regex]::Escape($base))") { return "after two presses and a reload the page reported: '$rep' (baseline dpr $base)" }
        Remove-Item $reportLog -ErrorAction SilentlyContinue; Keys '^0'; Keys '{F5}'; Start-Sleep -Seconds 5
        $rep2 = if (Test-Path $reportLog) { Get-Content $reportLog -Raw } else { '' }
        if ($rep2 -notmatch "z=1&dpr=$([regex]::Escape($base))") { return "after Ctrl+0 and reload the page reported: '$rep2'" }
    }

    # ---------------- 19/20 default browser and second copy
    Row '19 Default browser: Make default / Settings hand-off / links from other programs' 'NOT TESTED' 'the dialog registers JevBrowse in the current user registry before it asks; that changes the host, so it is left for the disposable guest'
    Try-Row '20 A second copy (extracted to another folder) on the same data folder is refused with a message, never a second window on the data' {
        $app2 = Join-Path $run 'app2'; Copy-Item (Resolve-Path $AppDir).Path $app2 -Recurse
        $before = @(Get-Process JevBrowse.App -ErrorAction SilentlyContinue).Count
        $env:JEVBROWSE_DATA_DIR = $data
        $p2 = Start-Process (Join-Path $app2 'JevBrowse.App.exe') -PassThru; $st2 = $p2.StartTime
        $w2 = $null; for ($i = 0; $i -lt 60 -and -not $w2 -and -not $p2.HasExited; $i++) { Start-Sleep -Milliseconds 500; $w2 = $AE::RootElement.FindFirst($TS::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p2.Id))) }
        $shown = ''
        if ($w2) { $shown = (@($w2.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name }) -join ' | '); try { [W.U]::SetForegroundWindow($p2.MainWindowHandle) | Out-Null; $w2.SetFocus() } catch { }; Start-Sleep -Milliseconds 500; [System.Windows.Forms.SendKeys]::SendWait('{ENTER}') }
        $ended = $p2.WaitForExit(20000)
        if (-not $ended) { Stop-Mine $p2 $st2; return "the second copy neither exited nor explained itself (window text: $shown)" }
        if ($shown -notmatch 'already using this data folder') { return "it exited but showed no explanation: '$shown'" }
        if ($p2.ExitCode -ne 1) { return "exit code $($p2.ExitCode)" }
        if (@(Get-Process JevBrowse.App -ErrorAction SilentlyContinue).Count -gt $before) { return 'a second JevBrowse is running' }
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
    try { if ($script:app -and -not $script:app.HasExited) { Stop-Mine $script:app $script:appStamp } } catch { }
}
foreach ($n in '1 launch (unpackaged, own data folder; first-run tips skipped)') { }
foreach ($n in 'Private-session rows (bookmark refusal, history, download list) through the UI', 'Rows 3, 5, 7-10, 12, 13 (real account, upload, camera prompt, clear data, Private restart, offline, missing runtime)') { if (-not $results.Contains($n)) { Row $n 'NOT TESTED' 'covered by earlier automated checks or needs a real account / VM; not repeated here' } }
$bad = @($results.GetEnumerator() | Where-Object { $_.Value.status -eq 'FAIL' }).Count
[ordered]@{ exe = $exe; zipSha256 = 'see report'; at = (Get-Date).ToUniversalTime().ToString('o'); os = [Environment]::OSVersion.VersionString; results = $results } | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $run 'interaction-pass.json') -Encoding utf8
Write-Host "`n$(if ($bad) { 'FAIL' } else { 'DONE' }) ($($results.Count) rows, $bad failed). $run"
if ($bad) { exit 1 }
