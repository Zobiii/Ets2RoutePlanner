using Ets2RoutePlanner.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ets2RoutePlanner.Data;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRoutePlannerData(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<AppDbContext>(o => o.UseSqlite(connectionString));
        services.AddSingleton<ImportProgress>();
        services.AddScoped<IImportCoordinator, ImportCoordinator>();
        services.AddScoped<IRecommendationService, RecommendationService>();
        services.AddScoped<ICompanyMappingService, CompanyMappingService>();
        return services;
    }
}
