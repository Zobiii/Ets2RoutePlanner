# ETS2 Route Planner

Standalone C# solution for Euro Truck Simulator 2 cargo route suggestions:

`StartCompany -> CargoType -> TargetCompany`

## Projects
- `Ets2RoutePlanner.Web`: Blazor Server UI
- `Ets2RoutePlanner.Core`: domain model and contracts
- `Ets2RoutePlanner.Data`: EF Core (SQLite), import pipeline, matching logic

## Requirements
- .NET 8 SDK
- An extracted ETS2 data folder that contains at least `def/`
  - The current importer reads extracted folders directly.
  - It does not depend on downloading external map tools.

## Build and Run
```bash
dotnet build Ets2RoutePlanner.sln
dotnet run --project Ets2RoutePlanner.Web
```

On startup, the app:
1. Creates `Ets2RoutePlanner.Web/App_Data` if missing.
2. Applies EF Core migrations.
3. Ensures required SQLite tables and indexes exist.

## First Import Workflow (`/setup`)
1. Open `/setup` (default start page redirects there).
2. Click `Full Auto Import`.
3. If auto-detection fails, select your extracted ETS2 folder in the built-in folder browser, then click `Use Current Folder`.
4. Optional: click `Validate Folder` before importing.
5. Watch live import logs and summary counts in the same page.

`Clear DB` removes the current SQLite database and recreates schema.

## Pages
- `/setup`: run import, validate folder path, clear database, view live logs
- `/mapping`: map unmapped company aliases to known ETS2 companies (top-5 suggestions)
- `/suggest`: enter start and target city, then compute compatible cargo routes

If unmapped companies remain, `/suggest` shows a warning that results can be incomplete.

## SQLite Location
- `Ets2RoutePlanner.Web/App_Data/ets2routeplanner.db`

## Troubleshooting
- If import fails, verify the selected folder includes `def/`.
- If auto-detection cannot find ETS2, choose the folder manually on `/setup`.
- Use `/mapping` after import to resolve aliases and improve route quality.

## Legal
- The app does not download ETS2 proprietary assets.
- It reads local, user-provided extracted ETS2 data only.
