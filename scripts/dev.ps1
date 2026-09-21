<#
.SYNOPSIS
  One entry point for the everyday developer tasks.
    .\scripts\dev.ps1 test     all unit tests
    .\scripts\dev.ps1 build    build the app (Debug, x64)
    .\scripts\dev.ps1 run      build, then start the app with a throwaway data folder (set JEVBROWSE_DATA_DIR to keep your own)
    .\scripts\dev.ps1 check    tests, a Release build, and the real-engine checks that need no setup (additions-check, external links)
  Every step stops at the first failure and says which one. Nothing here changes anything outside the repository and its data folder.
#>
param([Parameter(Position = 0)][ValidateSet('test', 'build', 'run', 'check')][string]$Task = 'test')

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    if (Test-Path (Join-Path $PSScriptRoot 'env.ps1')) { . (Join-Path $PSScriptRoot 'env.ps1') }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'The .NET 10 SDK was not found. Install it from https://dot.net, or point scripts\env.ps1 at your copy.' }
}
Push-Location $root
try {
    function Step($name, [scriptblock]$body) {
        Write-Host "==> $name" -ForegroundColor Cyan
        & $body
        if ($LASTEXITCODE -ne 0) { throw "FAILED: $name (exit $LASTEXITCODE)" }
    }
    $debugExe = Join-Path $root 'artifacts\bin\JevBrowse.App\debug_win-x64\JevBrowse.App.exe'
    switch ($Task) {
        'test'  { Step 'unit tests' { dotnet test --nologo -v q } }
        'build' { Step 'build (Debug, x64)' { dotnet build src/JevBrowse.App -p:Platform=x64 --nologo -v q } }
        'run' {
            Step 'build (Debug, x64)' { dotnet build src/JevBrowse.App -p:Platform=x64 --nologo -v q }
            if (-not $env:JEVBROWSE_DATA_DIR) { $env:JEVBROWSE_DATA_DIR = Join-Path $root 'artifacts\dev-data' }
            Write-Host "data folder: $env:JEVBROWSE_DATA_DIR"
            & $debugExe
        }
        'check' {
            Step 'unit tests' { dotnet test --nologo -v q }
            Step 'build (Release, x64)' { dotnet build src/JevBrowse.App -c Release -p:Platform=x64 --nologo -v q }
            $release = Join-Path $root 'artifacts\bin\JevBrowse.App\release_win-x64\JevBrowse.App.exe'
            Step 'external links (two real processes)' { & (Join-Path $PSScriptRoot 'external-link-check.ps1') -ExePath $release }
            Write-Host 'All checks passed. (Crash recovery, the packaged smoke and the performance checks are in scripts\ and run in CI or before a release.)' -ForegroundColor Green
        }
    }
}
finally { Pop-Location }
