# ETS2 Route Planner

Standalone C# solution for Euro Truck Simulator 2 route cargo suggestions:

`StartCompany -> CargoType -> TargetCompany`

Stack:
- `Blazor Server` (`Ets2RoutePlanner.Web`)
- `Domain/logic` (`Ets2RoutePlanner.Core`)
- `EF Core + SQLite + import pipeline` (`Ets2RoutePlanner.Data`)

## Build & Run
```bash
dotnet build Ets2RoutePlanner.sln
dotnet run --project Ets2RoutePlanner.Web
```

The app applies EF Core migrations automatically on startup.

## Auto Import Flow (`/setup`)
1. Detect ETS2 path automatically.
2. Download/verify `ts-map` from official GitHub releases referenced by `https://unicor-p.github.io/ts-map/`.
3. Run `ts-map export`.
4. Parse city/depot export and build `CityCompany` by nearest city (`RADIUS_KM = 25`).
5. Parse local ETS2 `def.scs` + `dlc_*.scs` archives for cargo types + company in/out cargo rules.
6. Reconcile ts-map aliases to ETS2 internal company keys.
7. Save to SQLite and show summary counts.

Import runs in a hosted background service and streams live log lines to `/setup`.

## ETS2 Path Auto-Detection
Detection checks:
- Windows Steam roots, including common fixed-drive locations.
- `steamapps/libraryfolders.vdf` parsing.
- ETS2 app manifest `appmanifest_227300.acf`.
- Install folder validation by `def.scs`.
- Linux/Proton common Steam locations.

If detection fails, `/setup` shows a web folder picker (server-side browsing) so you can select the ETS2 root and rerun import.

## SQLite Location
Database file:
- `Ets2RoutePlanner.Web/App_Data/ets2routeplanner.db`

`Clear DB` on `/setup` deletes the SQLite file and recreates schema.

## Mapping & Suggest
- `/mapping`: map unmapped ts-map aliases to internal ETS2 companies (top-5 suggestions shown).
- `/suggest`: autocomplete start/target city and compute valid intersections:
  - start company must have `Out(cargo)`
  - target company must have `In(cargo)`

If unmapped depots still exist, `/suggest` displays a warning banner.

## Troubleshooting
- Ensure ETS2 is installed locally and contains `def.scs`.
- Ensure internet access is available for first `ts-map` download.
- If ts-map output format changes, rerun import (parser supports GeoJSON/JSON heuristics).
- Use `/mapping` to reduce unmapped aliases and improve suggestion completeness.

## Legal
- The app does **not** download ETS2 proprietary assets.
- It only reads local installed game archives (`def.scs`, `dlc_*.scs`).
