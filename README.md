# JevBrowse

Open-source adaptive-computing browser for Windows. See `docs/PRODUCT_CONSTITUTION.md`, `docs/BUILD_PLAN.md`.

## Build (everything stays on D:)

```powershell
. .\scripts\env.ps1                       # SDK, NuGet cache, temp, data dir -> D:
dotnet test                               # unit tests
dotnet build src/JevBrowse.App -p:Platform=x64
.\artifacts\bin\JevBrowse.App\debug_win-x64\JevBrowse.App.exe
```

The .NET 10 SDK is installed at `D:\Tools\dotnet` (via `dotnet-install.ps1`, no admin).
AI keys (`JEV_API_KEY`, `OPENROUTER_API_KEY`) are read from the environment; `env.ps1` loads only those from the dashboard `.env` for dev. AI is off by default.
