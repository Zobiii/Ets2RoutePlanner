# ETS2 Route Planner

Blazor Server + ASP.NET Core + EF Core + SQLite app that imports ETS2 local data and ts-map exports to suggest:

`StartCompany -> CargoType -> TargetCompany`

## Projects
- `Ets2RoutePlanner.Web` (Blazor Server UI)
- `Ets2RoutePlanner.Core` (domain models)
- `Ets2RoutePlanner.Data` (EF Core, import, parsers, ts-map automation)

## Run
```bash
dotnet build Ets2RoutePlanner.sln
dotnet run --project Ets2RoutePlanner.Web
```

## Auto-detection
The importer checks Steam library folders and app manifest `227300` and validates `def.scs` inside:
- Windows: `C:\Program Files (x86)\Steam\...` + alternate steam roots
- Linux/Proton: `~/.steam/steam/...` and `~/.local/share/Steam/...`

If auto-detection fails, `/setup` asks for the ETS2 folder.

## Database location
SQLite file is created at:
- `Ets2RoutePlanner.Web/App_Data/ets2routeplanner.db`

## Import troubleshooting
- Ensure ETS2 is installed and `def.scs` exists in the selected folder.
- Ensure internet access for ts-map release download (cached under `tools/ts-map/`).
- If ts-map export format differs, re-run import after tool updates.
- Use `/mapping` to resolve unmapped depots and improve suggestions.

## Notes
- No proprietary game assets are downloaded from internet.
- Re-run import is idempotent (upserts and unique constraints).
