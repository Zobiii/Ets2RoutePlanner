using Ets2RoutePlanner.Core;
using Microsoft.EntityFrameworkCore;

namespace Ets2RoutePlanner.Data;

public class RecommendationService(AppDbContext db) : IRecommendationService
{
    public async Task<SuggestionResult> SuggestAsync(string startCityName, string targetCityName, CancellationToken ct = default)
    {
        var cities = await db.Cities.ToListAsync(ct);
        var start = cities.FirstOrDefault(c => c.Name.Equals(startCityName, StringComparison.OrdinalIgnoreCase));
        var end = cities.FirstOrDefault(c => c.Name.Equals(targetCityName, StringComparison.OrdinalIgnoreCase));
        if (start is null || end is null)
        {
            var startHints = cities.Select(c => c.Name).OrderBy(n => Fuzzy.Score(startCityName.ToLowerInvariant(), n.ToLowerInvariant())).TakeLast(5).ToList();
            var targetHints = cities.Select(c => c.Name).OrderBy(n => Fuzzy.Score(targetCityName.ToLowerInvariant(), n.ToLowerInvariant())).TakeLast(5).ToList();
            return new SuggestionResult([], startHints, targetHints);
        }

        var startCompanies = await db.CityCompanies.Where(x => x.CityId == start.Id).Select(x => x.CompanyId).ToListAsync(ct);
        var endCompanies = await db.CityCompanies.Where(x => x.CityId == end.Id).Select(x => x.CompanyId).ToListAsync(ct);
        var rules = await db.CompanyCargoRules.Include(x => x.CargoType).ToListAsync(ct);
        var companies = await db.Companies.ToDictionaryAsync(x => x.Id, ct);
        var suggestions = new HashSet<RouteSuggestion>();
        foreach (var sc in startCompanies)
        {
            var outC = rules.Where(r => r.CompanyId == sc && r.Direction == CargoDirection.Out).ToDictionary(r => r.CargoTypeId);
            foreach (var ec in endCompanies)
            {
                foreach (var inc in rules.Where(r => r.CompanyId == ec && r.Direction == CargoDirection.In))
                {
                    if (!outC.TryGetValue(inc.CargoTypeId, out var outRule)) continue;
                    suggestions.Add(new RouteSuggestion(companies[sc].DisplayName ?? companies[sc].Key, inc.CargoType?.DisplayName ?? inc.CargoType?.Key ?? outRule.CargoType?.Key ?? "", companies[ec].DisplayName ?? companies[ec].Key));
                }
            }
        }
        var sorted = suggestions.OrderBy(x => x.StartCompany).ThenBy(x => x.CargoType).ThenBy(x => x.TargetCompany).ToList();
        return new SuggestionResult(sorted, [], []);
    }
}

public record MappingSuggestion(string AliasKey, string DisplayName, IReadOnlyList<string> Candidates);
public interface ICompanyMappingService
{
    Task<IReadOnlyList<MappingSuggestion>> GetUnmappedAsync(CancellationToken ct = default);
    Task ApplyMappingAsync(string aliasKey, int targetCompanyId, CancellationToken ct = default);
}

public class CompanyMappingService(AppDbContext db) : ICompanyMappingService
{
    public async Task<IReadOnlyList<MappingSuggestion>> GetUnmappedAsync(CancellationToken ct = default)
    {
        var companies = await db.Companies.ToListAsync(ct);
        var mapped = companies.Where(x => !x.IsUnmapped).ToList();
        return companies.Where(x => x.IsUnmapped).Select(u => new MappingSuggestion(
            u.Key,
            u.DisplayName ?? u.Key,
            mapped.Select(m => new { m.Key, Score = Fuzzy.Score(CompanyNormalizer.Simplify(u.Key), CompanyNormalizer.Simplify(m.Key)) })
                .OrderByDescending(x => x.Score).Take(5).Select(x => x.Key).ToList()
        )).ToList();
    }

    public async Task ApplyMappingAsync(string aliasKey, int targetCompanyId, CancellationToken ct = default)
    {
        var source = await db.Companies.FirstAsync(x => x.Key == aliasKey, ct);
        if (!await db.CompanyAliases.AnyAsync(x => x.AliasKey == aliasKey, ct))
            db.CompanyAliases.Add(new CompanyAlias { AliasKey = aliasKey, CompanyId = targetCompanyId, Source = "manual" });

        var links = await db.CityCompanies.Where(x => x.CompanyId == source.Id).ToListAsync(ct);
        foreach (var l in links)
        {
            if (!await db.CityCompanies.AnyAsync(x => x.CityId == l.CityId && x.CompanyId == targetCompanyId, ct))
                db.CityCompanies.Add(new CityCompany { CityId = l.CityId, CompanyId = targetCompanyId, Source = CityCompanySource.Manual });
            db.CityCompanies.Remove(l);
        }
        db.Companies.Remove(source);
        await db.SaveChangesAsync(ct);
    }
}
