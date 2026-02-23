using Microsoft.EntityFrameworkCore;

namespace Ets2RoutePlanner.Data;

public static class DatabaseSchemaBootstrapper
{
    public static async Task EnsureSchemaAsync(AppDbContext db, CancellationToken ct = default)
    {
        await db.Database.MigrateAsync(ct);

        if (!db.Database.IsSqlite())
        {
            return;
        }

        var statements = new[]
        {
            """
            CREATE TABLE IF NOT EXISTS "Cities" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Cities" PRIMARY KEY AUTOINCREMENT,
                "Name" TEXT NOT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "Companies" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Companies" PRIMARY KEY AUTOINCREMENT,
                "Key" TEXT NOT NULL,
                "DisplayName" TEXT NULL,
                "IsUnmapped" INTEGER NOT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "CargoTypes" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_CargoTypes" PRIMARY KEY AUTOINCREMENT,
                "Key" TEXT NOT NULL,
                "DisplayName" TEXT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "ImportLogs" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_ImportLogs" PRIMARY KEY AUTOINCREMENT,
                "Kind" TEXT NOT NULL,
                "StartedAtUtc" TEXT NOT NULL,
                "EndedAtUtc" TEXT NULL,
                "Success" INTEGER NOT NULL,
                "Message" TEXT NOT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "CompanyAliases" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_CompanyAliases" PRIMARY KEY AUTOINCREMENT,
                "AliasKey" TEXT NOT NULL,
                "CompanyId" INTEGER NOT NULL,
                "Source" TEXT NOT NULL,
                CONSTRAINT "FK_CompanyAliases_Companies_CompanyId"
                    FOREIGN KEY ("CompanyId") REFERENCES "Companies" ("Id") ON DELETE CASCADE
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "CityCompanies" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_CityCompanies" PRIMARY KEY AUTOINCREMENT,
                "CityId" INTEGER NOT NULL,
                "CompanyId" INTEGER NOT NULL,
                "Source" INTEGER NOT NULL,
                CONSTRAINT "FK_CityCompanies_Cities_CityId"
                    FOREIGN KEY ("CityId") REFERENCES "Cities" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_CityCompanies_Companies_CompanyId"
                    FOREIGN KEY ("CompanyId") REFERENCES "Companies" ("Id") ON DELETE CASCADE
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "CompanyCargoRules" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_CompanyCargoRules" PRIMARY KEY AUTOINCREMENT,
                "CompanyId" INTEGER NOT NULL,
                "CargoTypeId" INTEGER NOT NULL,
                "Direction" INTEGER NOT NULL,
                CONSTRAINT "FK_CompanyCargoRules_Companies_CompanyId"
                    FOREIGN KEY ("CompanyId") REFERENCES "Companies" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_CompanyCargoRules_CargoTypes_CargoTypeId"
                    FOREIGN KEY ("CargoTypeId") REFERENCES "CargoTypes" ("Id") ON DELETE CASCADE
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_Companies_Key" ON "Companies" ("Key");""",
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_CargoTypes_Key" ON "CargoTypes" ("Key");""",
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_CompanyAliases_AliasKey" ON "CompanyAliases" ("AliasKey");""",
            """CREATE INDEX IF NOT EXISTS "IX_CompanyAliases_CompanyId" ON "CompanyAliases" ("CompanyId");""",
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_CityCompanies_CityId_CompanyId" ON "CityCompanies" ("CityId","CompanyId");""",
            """CREATE INDEX IF NOT EXISTS "IX_CityCompanies_CompanyId" ON "CityCompanies" ("CompanyId");""",
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_CompanyCargoRules_CompanyId_CargoTypeId_Direction" ON "CompanyCargoRules" ("CompanyId","CargoTypeId","Direction");""",
            """CREATE INDEX IF NOT EXISTS "IX_CompanyCargoRules_CargoTypeId" ON "CompanyCargoRules" ("CargoTypeId");"""
        };

        foreach (var sql in statements)
        {
            await db.Database.ExecuteSqlRawAsync(sql, ct);
        }
    }
}
