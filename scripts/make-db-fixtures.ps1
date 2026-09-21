<#
Regenerates tests/Fixtures/db/vN.db from the REAL historic build of each schema version (the BrowserDb.cs at the commit that introduced it),
plus tests/Tools/DbFixtureGen/Program.cs, which seeds the same rows in every version. Read-only on the repo apart from writing the fixtures.
Needs git history. Scratch projects go under D:\Browser\_ui-check\dbfix.
#>
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'env.ps1')
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$work = 'D:\Browser\_ui-check\dbfix'
$commits = @('5f89a5e', '022cec4', '0e734d5', 'c1a1612', '8e24e8a', 'bc1d14d', 'f8167c9', '8906d30', '679b233')   # index + 1 = schema version
foreach ($v in 1..9) {
    $c = $commits[$v - 1]; $dir = Join-Path $work "v$v"
    New-Item -ItemType Directory -Force $dir | Out-Null
    git -C $repo show "${c}:src/JevBrowse.Storage/BrowserDb.cs" | Set-Content "$dir\BrowserDb.cs" -Encoding utf8
    Copy-Item "$repo\tests\Tools\DbFixtureGen\Program.cs" "$dir\Program.cs" -Force
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.0" /><PackageReference Include="SQLitePCLRaw.bundle_e_sqlite3" Version="3.0.5" /></ItemGroup>
</Project>
'@ | Set-Content "$dir\gen.csproj" -Encoding utf8
    dotnet run --project "$dir\gen.csproj" -c Release -- $v "$repo\tests\Fixtures\db\v$v.db"
    if ($LASTEXITCODE -ne 0) { throw "fixture v$v failed (commit $c)" }
}
