using Ets2RoutePlanner.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
        services.AddSingleton<ImportHostedService>();
        services.AddSingleton<IImportJobService>(sp => sp.GetRequiredService<ImportHostedService>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<ImportHostedService>());
        return services;
    }
}
