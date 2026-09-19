# Dot-source per shell:  . D:\Browser\jevbrowse\scripts\env.ps1
# Keeps every toolchain/cache/data path on D: (C: is space-limited). Writes nothing to global env.
$env:DOTNET_ROOT              = 'D:\Tools\dotnet'
$env:DOTNET_CLI_HOME          = 'D:\Tools\dotnet-home'
$env:NUGET_PACKAGES           = 'D:\Tools\nuget\packages'
$env:NUGET_HTTP_CACHE_PATH    = 'D:\Tools\nuget\http-cache'
$env:NUGET_PLUGINS_CACHE_PATH = 'D:\Tools\nuget\plugins-cache'
$env:TEMP                     = 'D:\Tools\tmp'
$env:TMP                      = 'D:\Tools\tmp'
$env:DOTNET_NOLOGO            = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:JEVBROWSE_DATA_DIR       = 'D:\Browser\data'
$env:Path = "D:\Tools\dotnet;$env:Path"

# Dev-only: pull ONLY the two AI keys from the dashboard .env (never copy the file; it holds other secrets).
$devEnv = 'D:\Dashboard\backend\.env'
if (Test-Path $devEnv) {
  foreach ($line in Get-Content $devEnv) {
    if ($line -match '^\s*(JEV_API_KEY|OPENROUTER_API_KEY|OPENROUTER_MODEL)\s*=\s*(.*?)\s*$') {
      Set-Item -Path "env:$($Matches[1])" -Value $Matches[2].Trim('"').Trim("'")
    }
  }
}
foreach ($d in 'D:\Tools\nuget\packages','D:\Tools\nuget\http-cache','D:\Tools\nuget\plugins-cache','D:\Tools\tmp','D:\Browser\data') {
  New-Item -ItemType Directory -Force $d | Out-Null
}
