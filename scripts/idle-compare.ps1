<#
.SYNOPSIS
  Compares two result sets written by idle-benchmark.ps1 (results.json), scenario by scenario, on the shell's CPU, using the same rule the harness
  and its self-test use. Reads only; changes nothing. Exit code is always 0 unless -Enforce is given and a regression is found (then 1).
.PARAMETER Scenario   Scenario to take from each side. The candidate side may name a different one (-CandidateScenario) to compare, say, agent-idle with static.
#>
param(
    [Parameter(Mandatory)][string]$Candidate,
    [Parameter(Mandatory)][string]$Baseline,
    [string[]]$Scenario = @('static'),
    [string]$CandidateScenario = '',
    [switch]$Enforce
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'IdleBenchmark.Lib.ps1')
$c = Get-Content $Candidate -Raw | ConvertFrom-Json
$b = Get-Content $Baseline -Raw | ConvertFrom-Json
function ToHash($o) { if ($null -eq $o) { return $null }; $h = [ordered]@{}; foreach ($p in $o.PSObject.Properties) { $h[$p.Name] = $p.Value }; $h }
$worst = 0
foreach ($sc in ($Scenario -split ',')) {
    $csc = if ($CandidateScenario) { $CandidateScenario } else { $sc }
    $cs = $c.scenarios.$csc.summary; $bs = $b.scenarios.$sc.summary
    if ($null -eq $cs -or $null -eq $bs) { "$sc : inconclusive (a result set does not contain the scenario)"; continue }
    $r = Compare-Scenario (ToHash $cs) (ToHash $bs)
    "{0,-13} candidate {1} vs baseline {2}: {3}. {4}" -f $sc, $csc, $sc, $r.verdict.ToUpper(), $r.reason
    if ($r.verdict -eq 'regression') { $worst = 1 }
}
if ($Enforce) { exit $worst } else { exit 0 }
