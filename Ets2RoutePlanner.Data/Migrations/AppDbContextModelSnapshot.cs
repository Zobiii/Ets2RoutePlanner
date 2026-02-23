using Ets2RoutePlanner.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

#nullable disable
namespace Ets2RoutePlanner.Data.Migrations;

[DbContext(typeof(AppDbContext))]
partial class AppDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "8.0.8");
        modelBuilder.Entity("Ets2RoutePlanner.Core.City", b =>
        {
            b.Property<int>("Id").ValueGeneratedOnAdd();
            b.Property<string>("Name");
            b.HasKey("Id");
            b.ToTable("Cities");
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
    }
}
