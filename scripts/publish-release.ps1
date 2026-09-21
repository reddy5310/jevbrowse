<#
.SYNOPSIS
  Publishes a GitHub pre-release from EXACTLY the zip that was tested. It refuses unless the zip's SHA-256 equals the hash you say you tested, matches
  SHA256SUMS.txt, and BUILD.json inside the zip names the same version. Symbols are never attached (they carry build-machine paths).
  Nothing is uploaded without -Publish; without it the script only verifies and says what it would do.
.EXAMPLE
  .\scripts\publish-release.ps1 -Version 0.1.0-alpha.7 -TestedSha256 <hash from the clean-Windows run>          # verify only
  .\scripts\publish-release.ps1 -Version 0.1.0-alpha.7 -TestedSha256 <hash> -Publish                            # create the pre-release
  Use -Dir to point at a downloaded workflow artifact instead of artifacts\release\<version>.
#>
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$TestedSha256,
    [string]$Dir = '',
    [switch]$Publish
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Split-Path $PSScriptRoot -Parent
if (-not $Dir) { $Dir = Join-Path $root "artifacts\release\$Version" }
$zip = Join-Path $Dir "JevBrowse-$Version-win-x64.zip"
foreach ($f in @($zip, (Join-Path $Dir 'SHA256SUMS.txt'), (Join-Path $Dir 'sbom-packages.json'))) { if (-not (Test-Path $f)) { throw "missing $f" } }

$actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
if ($actual -ne $TestedSha256.ToLower()) { throw "This zip is $actual, not the tested $($TestedSha256.ToLower()). Nothing was published." }
$listed = ((Get-Content (Join-Path $Dir 'SHA256SUMS.txt') | Where-Object { $_ -match 'win-x64\.zip$' }) -replace '\s.*$', '')
if ($listed -ne $actual) { throw "SHA256SUMS.txt lists $listed for the zip. Nothing was published." }
$z = [IO.Compression.ZipFile]::OpenRead((Resolve-Path $zip))
try { $build = (New-Object IO.StreamReader(($z.Entries | Where-Object { $_.FullName -eq 'BUILD.json' }).Open())).ReadToEnd() | ConvertFrom-Json } finally { $z.Dispose() }
if ($build.version -ne $Version) { throw "BUILD.json says version $($build.version)." }
Write-Host "verified: $actual  (version $Version, commit $($build.commit))"

if (-not $Publish) { Write-Host 'Verify only. Add -Publish to create the GitHub pre-release from these exact files.'; return }
$gh = if (Get-Command gh -ErrorAction SilentlyContinue) { 'gh' } elseif (Test-Path 'D:\Tools\gh\bin\gh.exe') { 'D:\Tools\gh\bin\gh.exe' } else { throw 'the GitHub CLI (gh) was not found' }
& $gh release create "v$Version" --prerelease --target $build.commit --title "JevBrowse $Version" `
    --notes "Unsigned alpha. Verify the SHA-256 in SHA256SUMS.txt before running. What changed and what is not proven: docs/releases/ and docs/EVIDENCE_MATRIX.md." `
    $zip (Join-Path $Dir 'SHA256SUMS.txt') (Join-Path $Dir 'sbom-packages.json')
if ($LASTEXITCODE -ne 0) { throw 'gh release create failed' }
