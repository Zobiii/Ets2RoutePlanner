using Ets2RoutePlanner.Core;
using Microsoft.EntityFrameworkCore;

namespace Ets2RoutePlanner.Data;

public sealed class RecommendationService(AppDbContext db) : IRecommendationService
{
    public async Task<SuggestionResult> SuggestAsync(string startCityName, string targetCityName, CancellationToken ct = default)
    {
        var cities = await db.Cities
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

        var start = ResolveCity(cities, startCityName);
        var target = ResolveCity(cities, targetCityName);

        if (start is null || target is null)
        {
            return new SuggestionResult(
                [],
                BuildCityHints(cities, startCityName),
                BuildCityHints(cities, targetCityName));
        }

        var startCompanyIds = await db.CityCompanies
            .AsNoTracking()
            .Where(x => x.CityId == start.Id)
            .Select(x => x.CompanyId)
            .Distinct()
            .ToListAsync(ct);

        var targetCompanyIds = await db.CityCompanies
            .AsNoTracking()
            .Where(x => x.CityId == target.Id)
            .Select(x => x.CompanyId)
            .Distinct()
            .ToListAsync(ct);

        if (startCompanyIds.Count == 0 || targetCompanyIds.Count == 0)
        {
            return new SuggestionResult([], [], []);
        }

        var involvedCompanyIds = startCompanyIds
            .Concat(targetCompanyIds)
            .Distinct()
            .ToList();

        var rules = await db.CompanyCargoRules
            .AsNoTracking()
            .Include(r => r.CargoType)
            .Where(r => involvedCompanyIds.Contains(r.CompanyId))
            .ToListAsync(ct);

        var companies = await db.Companies
            .AsNoTracking()
            .Where(c => involvedCompanyIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, ct);

        var outRulesByCompany = rules
            .Where(r => r.Direction == CargoDirection.Out)
            .GroupBy(r => r.CompanyId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(r => r.CargoTypeId));

        var inRulesByCompany = rules
            .Where(r => r.Direction == CargoDirection.In)
            .GroupBy(r => r.CompanyId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var suggestions = new HashSet<RouteSuggestion>();

        foreach (var startCompanyId in startCompanyIds)
        {
            if (!outRulesByCompany.TryGetValue(startCompanyId, out var outRules))
            {
                continue;
            }

            foreach (var targetCompanyId in targetCompanyIds)
            {
                if (!inRulesByCompany.TryGetValue(targetCompanyId, out var inRules))
                {
                    continue;
                }

                foreach (var inRule in inRules)
                {
                    if (!outRules.TryGetValue(inRule.CargoTypeId, out var outRule))
                    {
                        continue;
                    }

                    var startCompany = companies[startCompanyId].DisplayName ?? companies[startCompanyId].Key;
                    var cargo = inRule.CargoType?.DisplayName ?? outRule.CargoType?.DisplayName ?? inRule.CargoType?.Key ?? outRule.CargoType?.Key ?? string.Empty;
                    var targetCompany = companies[targetCompanyId].DisplayName ?? companies[targetCompanyId].Key;

                    suggestions.Add(new RouteSuggestion(startCompany, cargo, targetCompany));
                }
            }
        }

        var ordered = suggestions
            .OrderBy(s => s.StartCompany, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.CargoType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.TargetCompany, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SuggestionResult(ordered, [], []);
    }

    private static City? ResolveCity(List<City> cities, string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var exact = cities.FirstOrDefault(c => c.Name.Equals(input.Trim(), StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var best = cities
            .Select(c => new { City = c, Score = Fuzzy.Score(input, c.Name) })
            .OrderByDescending(x => x.Score)
            .FirstOrDefault();

        return best is { Score: >= 0.92 } ? best.City : null;
    }

    private static IReadOnlyList<string> BuildCityHints(List<City> cities, string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return [];
        }

        return cities
            .Select(c => new { c.Name, Score = Fuzzy.Score(input, c.Name) })
            .OrderByDescending(x => x.Score)
            .Take(5)
            .Select(x => x.Name)
            .ToList();
    }
}

public record MappingSuggestion(string AliasKey, string DisplayName, IReadOnlyList<string> Candidates);

public interface ICompanyMappingService
{
    Task<IReadOnlyList<MappingSuggestion>> GetUnmappedAsync(CancellationToken ct = default);
    Task ApplyMappingAsync(string aliasKey, int targetCompanyId, CancellationToken ct = default);
}

public sealed class CompanyMappingService(AppDbContext db) : ICompanyMappingService
{
    public async Task<IReadOnlyList<MappingSuggestion>> GetUnmappedAsync(CancellationToken ct = default)
    {
        var companies = await db.Companies
            .AsNoTracking()
            .OrderBy(c => c.Key)
            .ToListAsync(ct);

        var mapped = companies.Where(c => !c.IsUnmapped).ToList();
        var unmapped = companies.Where(c => c.IsUnmapped).ToList();

        return unmapped.Select(u =>
            new MappingSuggestion(
                u.Key,
                u.DisplayName ?? u.Key,
                mapped
                    .Select(m => new { m.Key, Score = Fuzzy.Score(CompanyNormalizer.Simplify(u.Key), CompanyNormalizer.Simplify(m.Key)) })
                    .OrderByDescending(x => x.Score)
                    .Take(5)
                    .Select(x => x.Key)
                    .ToList()))
            .ToList();
    }

    public async Task ApplyMappingAsync(string aliasKey, int targetCompanyId, CancellationToken ct = default)
    {
        var target = await db.Companies.FirstOrDefaultAsync(c => c.Id == targetCompanyId, ct)
            ?? throw new InvalidOperationException("Target company not found.");

        var alias = await db.CompanyAliases.FirstOrDefaultAsync(a => a.AliasKey == aliasKey, ct);
        Company? sourceCompany = null;

        if (alias is null)
        {
            sourceCompany = await db.Companies.FirstOrDefaultAsync(c => c.Key == aliasKey, ct);
            if (sourceCompany is null)
            {
                throw new InvalidOperationException("Alias source company not found.");
            }

            alias = new CompanyAlias
            {
                AliasKey = aliasKey,
                CompanyId = targetCompanyId,
                Source = "manual"
            };
            db.CompanyAliases.Add(alias);
        }
        else
        {
            sourceCompany = await db.Companies.FirstOrDefaultAsync(c => c.Id == alias.CompanyId, ct);
            alias.CompanyId = targetCompanyId;
            alias.Source = "manual";
        }

        if (sourceCompany is not null && sourceCompany.Id != targetCompanyId)
        {
            var cityLinks = await db.CityCompanies
                .Where(x => x.CompanyId == sourceCompany.Id)
                .ToListAsync(ct);

            foreach (var link in cityLinks)
            {
                var exists = await db.CityCompanies.AnyAsync(
                    x => x.CityId == link.CityId && x.CompanyId == targetCompanyId,
                    ct);

                if (!exists)
                {
                    db.CityCompanies.Add(new CityCompany
                    {
                        CityId = link.CityId,
                        CompanyId = targetCompanyId,
                        Source = CityCompanySource.Manual
                    });
                }

                db.CityCompanies.Remove(link);
            }

            var sourceAliases = await db.CompanyAliases
                .Where(a => a.CompanyId == sourceCompany.Id)
                .ToListAsync(ct);

            foreach (var sourceAlias in sourceAliases)
            {
                sourceAlias.CompanyId = targetCompanyId;
                sourceAlias.Source = "manual";
            }

            var hasRules = await db.CompanyCargoRules.AnyAsync(r => r.CompanyId == sourceCompany.Id, ct);
            if (!hasRules)
            {
                db.Companies.Remove(sourceCompany);
            }
        }

        target.IsUnmapped = false;
        await db.SaveChangesAsync(ct);
    }
}
