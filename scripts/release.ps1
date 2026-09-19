# Reproducible portable build: self-contained x64 folder, zipped, with SHA-256 checksums and a package SBOM.
# Usage:  . .\scripts\env.ps1 ; .\scripts\release.ps1 -Version 0.1.0-alpha.1
param([Parameter(Mandatory)][string]$Version)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "artifacts\release\$Version"
$app = Join-Path $out "JevBrowse-$Version-win-x64"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $app | Out-Null

Push-Location $root
try {
  dotnet test --nologo
  if ($LASTEXITCODE -ne 0) { throw "tests failed" }
  dotnet publish src/JevBrowse.App -c Release -r win-x64 -p:Platform=x64 -p:SelfContained=true -p:WindowsAppSDKSelfContained=true -p:PublishReadyToRun=false -o $app --nologo
  if ($LASTEXITCODE -ne 0) { throw "publish failed" }

  Copy-Item README.md, CONTRIBUTING.md, SECURITY.md, GOVERNANCE.md -Destination $app
  if (Test-Path LICENSE) { Copy-Item LICENSE $app }
  @{ version = $Version; commit = (git rev-parse HEAD); builtAt = (Get-Date).ToString('o'); dotnet = (dotnet --version) } | ConvertTo-Json | Set-Content (Join-Path $app 'BUILD.json') -Encoding utf8

  # SBOM: every NuGet package in the build graph.
  dotnet list src/JevBrowse.App/JevBrowse.App.csproj package --include-transitive --format json > (Join-Path $out 'sbom-packages.json')

  $zip = Join-Path $out "JevBrowse-$Version-win-x64.zip"
  Compress-Archive -Path "$app\*" -DestinationPath $zip -CompressionLevel Optimal
  Get-ChildItem $out -File | ForEach-Object { "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name } | Set-Content (Join-Path $out 'SHA256SUMS.txt') -Encoding ascii

  "release at $out"
  Get-Content (Join-Path $out 'SHA256SUMS.txt')
  "portable size: {0:N0} MB" -f ((Get-Item $zip).Length / 1MB)
}
finally { Pop-Location }
