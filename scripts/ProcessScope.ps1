# Shared by every script that starts the app and cleans up after it.
#
# Windows reuses process ids, so "a pid I saw earlier" is not proof that a process is one of mine (an unrelated app once inherited a freed id and was counted, and
# stopped, as a leftover). A process here is OURS only if ALL of these hold:
#   - it is the process this script started (held by a Process object, so its id cannot be reused while it is held), or it descends from that one through parent links
#     where the child started no earlier than its parent (a reused-id impostor started later and would fail this the other way round, and is re-checked below);
#   - at the moment of stopping, its id STILL has the same start time as when it was recorded.
# Nothing else is ever stopped, and no process is stopped by name.

function Get-OwnedProcesses([System.Diagnostics.Process]$Root) {
    $owned = New-Object System.Collections.Generic.List[object]
    $all = @(Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId, CreationDate)
    $rootRow = $all | Where-Object { $_.ProcessId -eq $Root.Id } | Select-Object -First 1
    if (-not $rootRow) { return @() }
    $owned.Add([pscustomobject]@{ Id = [int]$Root.Id; Start = [datetime]$rootRow.CreationDate })
    $queue = New-Object System.Collections.Generic.Queue[object]; $queue.Enqueue($owned[0])
    while ($queue.Count) {
        $parent = $queue.Dequeue()
        foreach ($c in ($all | Where-Object { $_.ParentProcessId -eq $parent.Id })) {
            if ($owned.Id -contains [int]$c.ProcessId) { continue }
            if ([datetime]$c.CreationDate -lt $parent.Start) { continue }   # cannot be a child of that parent: the id was reused
            $item = [pscustomobject]@{ Id = [int]$c.ProcessId; Start = [datetime]$c.CreationDate }
            $owned.Add($item); $queue.Enqueue($item)
        }
    }
    $owned.ToArray()
}

function Test-StillOwned($Item) {
    $p = Get-Process -Id $Item.Id -ErrorAction SilentlyContinue
    if (-not $p) { return $false }
    try { return [Math]::Abs(($p.StartTime - $Item.Start).TotalSeconds) -lt 2 } catch { return $false }
}

# Children first, then the root. Only items that are still the same process are stopped.
function Stop-OwnedProcesses($Items) {
    foreach ($i in @($Items | Sort-Object { $_.Start } -Descending)) { if (Test-StillOwned $i) { Stop-Process -Id $i.Id -Force -ErrorAction SilentlyContinue } }
}
