using System;
using Ets2RoutePlanner.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace Ets2RoutePlanner.Data.Migrations;

[DbContext(typeof(AppDbContext))]
partial class AppDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "8.0.8");

        modelBuilder.Entity("Ets2RoutePlanner.Core.CargoType", b =>
        {
            b.Property<int>("Id").ValueGeneratedOnAdd();
            b.Property<string>("DisplayName");
            b.Property<string>("Key");
            b.HasKey("Id");
            b.HasIndex("Key").IsUnique();
            b.ToTable("CargoTypes");
        });

        modelBuilder.Entity("Ets2RoutePlanner.Core.City", b =>
        {
            b.Property<int>("Id").ValueGeneratedOnAdd();
            b.Property<string>("Name");
            b.HasKey("Id");
            b.ToTable("Cities");
        });

        modelBuilder.Entity("Ets2RoutePlanner.Core.CityCompany", b =>
        {
            b.Property<int>("Id").ValueGeneratedOnAdd();
            b.Property<int>("CityId");
            b.Property<int>("CompanyId");
            b.Property<int>("Source");
            b.HasKey("Id");
            b.HasIndex("CityId", "CompanyId").IsUnique();
            b.HasIndex("CompanyId");
            b.ToTable("CityCompanies");
        });

        modelBuilder.Entity("Ets2RoutePlanner.Core.Company", b =>
        {
            b.Property<int>("Id").ValueGeneratedOnAdd();
            b.Property<string>("DisplayName");
            b.Property<bool>("IsUnmapped");
            b.Property<string>("Key");
            b.HasKey("Id");
            b.HasIndex("Key").IsUnique();
            b.ToTable("Companies");
        });

        modelBuilder.Entity("Ets2RoutePlanner.Core.CompanyAlias", b =>
        {
            b.Property<int>("Id").ValueGeneratedOnAdd();
            b.Property<string>("AliasKey");
            b.Property<int>("CompanyId");
            b.Property<string>("Source");
            b.HasKey("Id");
            b.HasIndex("AliasKey").IsUnique();
            b.HasIndex("CompanyId");
            b.ToTable("CompanyAliases");
        });

        modelBuilder.Entity("Ets2RoutePlanner.Core.CompanyCargoRule", b =>
        {
            b.Property<int>("Id").ValueGeneratedOnAdd();
            b.Property<int>("CargoTypeId");
            b.Property<int>("CompanyId");
            b.Property<int>("Direction");
            b.HasKey("Id");
            b.HasIndex("CargoTypeId");
            b.HasIndex("CompanyId", "CargoTypeId", "Direction").IsUnique();
            b.ToTable("CompanyCargoRules");
        });

        modelBuilder.Entity("Ets2RoutePlanner.Core.ImportLog", b =>
        {
            b.Property<int>("Id").ValueGeneratedOnAdd();
            b.Property<DateTime?>("EndedAtUtc");
            b.Property<string>("Kind");
            b.Property<string>("Message");
            b.Property<bool>("Success");
            b.Property<DateTime>("StartedAtUtc");
            b.HasKey("Id");
            b.ToTable("ImportLogs");
        });

        modelBuilder.Entity("Ets2RoutePlanner.Core.CityCompany", b =>
        {
            b.HasOne("Ets2RoutePlanner.Core.City", "City")
                .WithMany()
                .HasForeignKey("CityId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();

            b.HasOne("Ets2RoutePlanner.Core.Company", "Company")
                .WithMany()
                .HasForeignKey("CompanyId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
        });

        modelBuilder.Entity("Ets2RoutePlanner.Core.CompanyAlias", b =>
        {
            b.HasOne("Ets2RoutePlanner.Core.Company", "Company")
                .WithMany()
                .HasForeignKey("CompanyId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
        });

        modelBuilder.Entity("Ets2RoutePlanner.Core.CompanyCargoRule", b =>
        {
            b.HasOne("Ets2RoutePlanner.Core.CargoType", "CargoType")
                .WithMany()
                .HasForeignKey("CargoTypeId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();

            b.HasOne("Ets2RoutePlanner.Core.Company", "Company")
                .WithMany()
                .HasForeignKey("CompanyId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
        });
    }
}
