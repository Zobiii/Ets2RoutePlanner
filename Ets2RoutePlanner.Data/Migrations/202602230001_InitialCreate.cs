using System;
using Ets2RoutePlanner.Core;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace Ets2RoutePlanner.Data.Migrations;

public partial class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("Cities", table => new { Id = table.Column<int>(nullable: false).Annotation("Sqlite:Autoincrement", true), Name = table.Column<string>(nullable: false) }, constraints: table => table.PrimaryKey("PK_Cities", x => x.Id));
        migrationBuilder.CreateTable("Companies", table => new { Id = table.Column<int>(nullable: false).Annotation("Sqlite:Autoincrement", true), Key = table.Column<string>(nullable: false), DisplayName = table.Column<string>(nullable: true), IsUnmapped = table.Column<bool>(nullable: false) }, constraints: table => table.PrimaryKey("PK_Companies", x => x.Id));
        migrationBuilder.CreateTable("CargoTypes", table => new { Id = table.Column<int>(nullable: false).Annotation("Sqlite:Autoincrement", true), Key = table.Column<string>(nullable: false), DisplayName = table.Column<string>(nullable: true) }, constraints: table => table.PrimaryKey("PK_CargoTypes", x => x.Id));
        migrationBuilder.CreateTable("ImportLogs", table => new { Id = table.Column<int>(nullable: false).Annotation("Sqlite:Autoincrement", true), Kind = table.Column<string>(nullable: false), StartedAtUtc = table.Column<DateTime>(nullable: false), EndedAtUtc = table.Column<DateTime>(nullable: true), Success = table.Column<bool>(nullable: false), Message = table.Column<string>(nullable: false) }, constraints: table => table.PrimaryKey("PK_ImportLogs", x => x.Id));
        migrationBuilder.CreateTable("CompanyAliases", table => new { Id = table.Column<int>(nullable: false).Annotation("Sqlite:Autoincrement", true), AliasKey = table.Column<string>(nullable: false), CompanyId = table.Column<int>(nullable: false), Source = table.Column<string>(nullable: false) }, constraints: table => { table.PrimaryKey("PK_CompanyAliases", x => x.Id); table.ForeignKey("FK_CompanyAliases_Companies_CompanyId", x => x.CompanyId, "Companies", "Id", onDelete: ReferentialAction.Cascade); });
        migrationBuilder.CreateTable("CityCompanies", table => new { Id = table.Column<int>(nullable: false).Annotation("Sqlite:Autoincrement", true), CityId = table.Column<int>(nullable: false), CompanyId = table.Column<int>(nullable: false), Source = table.Column<int>(nullable: false) }, constraints: table => { table.PrimaryKey("PK_CityCompanies", x => x.Id); table.ForeignKey("FK_CityCompanies_Cities_CityId", x => x.CityId, "Cities", "Id", onDelete: ReferentialAction.Cascade); table.ForeignKey("FK_CityCompanies_Companies_CompanyId", x => x.CompanyId, "Companies", "Id", onDelete: ReferentialAction.Cascade); });
        migrationBuilder.CreateTable("CompanyCargoRules", table => new { Id = table.Column<int>(nullable: false).Annotation("Sqlite:Autoincrement", true), CompanyId = table.Column<int>(nullable: false), CargoTypeId = table.Column<int>(nullable: false), Direction = table.Column<int>(nullable: false) }, constraints: table => { table.PrimaryKey("PK_CompanyCargoRules", x => x.Id); table.ForeignKey("FK_CompanyCargoRules_Companies_CompanyId", x => x.CompanyId, "Companies", "Id", onDelete: ReferentialAction.Cascade); table.ForeignKey("FK_CompanyCargoRules_CargoTypes_CargoTypeId", x => x.CargoTypeId, "CargoTypes", "Id", onDelete: ReferentialAction.Cascade); });
        migrationBuilder.CreateIndex("IX_Companies_Key", "Companies", "Key", unique: true);
        migrationBuilder.CreateIndex("IX_CargoTypes_Key", "CargoTypes", "Key", unique: true);
        migrationBuilder.CreateIndex("IX_CompanyAliases_AliasKey", "CompanyAliases", "AliasKey", unique: true);
        migrationBuilder.CreateIndex("IX_CompanyAliases_CompanyId", "CompanyAliases", "CompanyId");
        migrationBuilder.CreateIndex("IX_CityCompanies_CityId_CompanyId", "CityCompanies", new[] { "CityId", "CompanyId" }, unique: true);
        migrationBuilder.CreateIndex("IX_CityCompanies_CompanyId", "CityCompanies", "CompanyId");
        migrationBuilder.CreateIndex("IX_CompanyCargoRules_CompanyId_CargoTypeId_Direction", "CompanyCargoRules", new[] { "CompanyId", "CargoTypeId", "Direction" }, unique: true);
        migrationBuilder.CreateIndex("IX_CompanyCargoRules_CargoTypeId", "CompanyCargoRules", "CargoTypeId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("CityCompanies");
        migrationBuilder.DropTable("CompanyAliases");
        migrationBuilder.DropTable("CompanyCargoRules");
        migrationBuilder.DropTable("ImportLogs");
        migrationBuilder.DropTable("Cities");
        migrationBuilder.DropTable("Companies");
        migrationBuilder.DropTable("CargoTypes");
    }
}
