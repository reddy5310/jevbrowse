# Tests scripts\ProcessScope.ps1 with real processes: an id that no longer means the same process must NOT be stopped. Exit 0 = passed.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ProcessScope.ps1')
$fails = 0
function Check($n, $ok) { if ($ok) { "ok   $n" } else { "FAIL $n"; $script:fails++ } }

$child = Start-Process powershell -ArgumentList '-NoProfile', '-Command', 'Start-Sleep 60' -PassThru -WindowStyle Hidden
$bystander = Start-Process powershell -ArgumentList '-NoProfile', '-Command', 'Start-Sleep 60' -PassThru -WindowStyle Hidden
try {
    Start-Sleep -Milliseconds 800
    $owned = @(Get-OwnedProcesses $child)
    Check 'the process we started is recorded with its start time' ($owned.Count -ge 1 -and $owned[0].Id -eq $child.Id -and $owned[0].Start -gt [datetime]'2000-01-01')
    Check 'an unrelated process is not in our tree' (@($owned | Where-Object { $_.Id -eq $bystander.Id }).Count -eq 0)

    # An id that has been REUSED: same id, different start time. It must survive.
    $impostor = [pscustomobject]@{ Id = $bystander.Id; Start = $bystander.StartTime.AddMinutes(-30) }
    Stop-OwnedProcesses @($impostor)
    Start-Sleep -Milliseconds 300
    Check 'an id whose start time does not match is NOT stopped' (-not $bystander.HasExited)

    # A stale record of a process that is gone: nothing happens, no error.
    Stop-OwnedProcesses @([pscustomobject]@{ Id = 2147000000; Start = Get-Date })
    Check 'a record of a process that no longer exists is harmless' $true

    Stop-OwnedProcesses $owned
    Start-Sleep -Milliseconds 500
    Check 'the process we started IS stopped' ($child.HasExited)
    Check 'and the unrelated process is untouched' (-not $bystander.HasExited)
}
finally { if (-not $bystander.HasExited) { $bystander.Kill() }; if (-not $child.HasExited) { $child.Kill() } }   # handles we hold: not id-based guesses
if ($fails) { "`n$fails FAILED"; exit 1 } else { "`nall passed"; exit 0 }
