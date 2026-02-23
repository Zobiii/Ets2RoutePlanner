using Ets2RoutePlanner.Core;
using Microsoft.EntityFrameworkCore;

namespace Ets2RoutePlanner.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<City> Cities => Set<City>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<CompanyAlias> CompanyAliases => Set<CompanyAlias>();
    public DbSet<CargoType> CargoTypes => Set<CargoType>();
    public DbSet<CompanyCargoRule> CompanyCargoRules => Set<CompanyCargoRule>();
    public DbSet<CityCompany> CityCompanies => Set<CityCompany>();
    public DbSet<ImportLog> ImportLogs => Set<ImportLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Company>().HasIndex(x => x.Key).IsUnique();
        b.Entity<CargoType>().HasIndex(x => x.Key).IsUnique();
        b.Entity<CompanyAlias>().HasIndex(x => x.AliasKey).IsUnique();
        b.Entity<CompanyCargoRule>().HasIndex(x => new { x.CompanyId, x.CargoTypeId, x.Direction }).IsUnique();
        b.Entity<CityCompany>().HasIndex(x => new { x.CityId, x.CompanyId }).IsUnique();

        b.Entity<CompanyCargoRule>().HasOne(x => x.Company).WithMany().HasForeignKey(x => x.CompanyId);
        b.Entity<CompanyCargoRule>().HasOne(x => x.CargoType).WithMany().HasForeignKey(x => x.CargoTypeId);
        b.Entity<CityCompany>().HasOne(x => x.City).WithMany().HasForeignKey(x => x.CityId);
        b.Entity<CityCompany>().HasOne(x => x.Company).WithMany().HasForeignKey(x => x.CompanyId);
        b.Entity<CompanyAlias>().HasOne(x => x.Company).WithMany().HasForeignKey(x => x.CompanyId);
    }
}
