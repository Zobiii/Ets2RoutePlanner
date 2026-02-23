using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ets2RoutePlanner.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Ets2RoutePlanner.Data;

public record ImportSummary(int Cities, int Companies, int CityCompanies, int CargoTypes, int Rules, int UnmappedCompanies);
public interface IImportCoordinator
{
    Task<ImportSummary> RunFullImportAsync(string? ets2Path = null, CancellationToken ct = default);
    Task ClearDatabaseAsync(CancellationToken ct = default);
    Task<string?> DetectEts2PathAsync(CancellationToken ct = default);
}

public class ImportProgress
{
    public event Action<string>? OnLog;
    public void Log(string message) => OnLog?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");
}

public class ImportCoordinator(AppDbContext db, ILogger<ImportCoordinator> logger, ImportProgress progress) : IImportCoordinator
{
    public const double RadiusKm = 25d;
    public async Task<string?> DetectEts2PathAsync(CancellationToken ct = default)
    {
        var candidates = Ets2PathDetector.GetCandidateRoots();
        foreach (var root in candidates)
        {
            var found = Ets2PathDetector.TryFindEts2(root);
            if (found is not null) return found;
        }
        await Task.CompletedTask;
        return null;
    }

    public async Task<ImportSummary> RunFullImportAsync(string? ets2Path = null, CancellationToken ct = default)
    {
        var log = new ImportLog { Kind = "full", StartedAtUtc = DateTime.UtcNow };
        db.ImportLogs.Add(log);
        await db.SaveChangesAsync(ct);
        try
        {
            progress.Log("Detect ETS2 path");
            ets2Path ??= await DetectEts2PathAsync(ct) ?? throw new InvalidOperationException("ETS2 path not detected");
            if (!File.Exists(Path.Combine(ets2Path, "def.scs"))) throw new InvalidOperationException("Invalid ETS2 path: def.scs missing");

            progress.Log("Download/verify ts-map");
            var tsMap = new TsMapRunner(logger, progress);
            var tsMapExe = await tsMap.EnsureTsMapAsync(ct);
            progress.Log("Run ts-map export");
            var exportFolder = await tsMap.RunExportAsync(tsMapExe, ets2Path, ct);

            progress.Log("Parse ts-map cities and depots");
            var mapData = TsMapParser.Parse(exportFolder);
            await UpsertCitiesAndCompaniesFromMapAsync(mapData, ct);

            progress.Log("Parse def.scs + dlc archives");
            var parsed = Ets2DefParser.ParseFromInstall(ets2Path, progress);
            await UpsertCargoAndRulesAsync(parsed, ct);

            progress.Log("Reconcile companies");
            await ReconcileAsync(ct);

            var summary = await BuildSummary(ct);
            log.Success = true;
            log.Message = JsonSerializer.Serialize(summary);
            log.EndedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            progress.Log("Import complete");
            return summary;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Import failed");
            log.Success = false;
            log.Message = ex.ToString();
            log.EndedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            throw;
        }
    }

    public async Task ClearDatabaseAsync(CancellationToken ct = default)
    {
        await db.Database.EnsureDeletedAsync(ct);
        await db.Database.EnsureCreatedAsync(ct);
    }

    private async Task UpsertCitiesAndCompaniesFromMapAsync(TsMapData mapData, CancellationToken ct)
    {
        var cities = await db.Cities.ToListAsync(ct);
        foreach (var city in mapData.Cities)
        {
            if (cities.All(c => !string.Equals(c.Name, city.Name, StringComparison.OrdinalIgnoreCase)))
                db.Cities.Add(new City { Name = city.Name });
        }
        await db.SaveChangesAsync(ct);
        cities = await db.Cities.ToListAsync(ct);

        foreach (var depot in mapData.Depots)
        {
            var aliasKey = CompanyNormalizer.Normalize(depot.Name);
            var alias = await db.CompanyAliases.Include(x => x.Company).FirstOrDefaultAsync(x => x.AliasKey == aliasKey, ct);
            Company company;
            if (alias is not null) company = alias.Company!;
            else
            {
                company = await db.Companies.FirstOrDefaultAsync(x => x.Key == aliasKey, ct) ?? new Company { Key = aliasKey, DisplayName = depot.Name, IsUnmapped = true };
                if (company.Id == 0) db.Companies.Add(company);
                await db.SaveChangesAsync(ct);
                db.CompanyAliases.Add(new CompanyAlias { AliasKey = aliasKey, CompanyId = company.Id, Source = "ts-map" });
                await db.SaveChangesAsync(ct);
            }

            var city = Geo.NearestCity(depot.Lat, depot.Lon, mapData.Cities, RadiusKm);
            if (city is null) continue;
            var cityEntity = cities.First(c => c.Name.Equals(city.Name, StringComparison.OrdinalIgnoreCase));
            var exists = await db.CityCompanies.AnyAsync(x => x.CityId == cityEntity.Id && x.CompanyId == company.Id, ct);
            if (!exists) db.CityCompanies.Add(new CityCompany { CityId = cityEntity.Id, CompanyId = company.Id, Source = CityCompanySource.TsMap });
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task UpsertCargoAndRulesAsync(Ets2ParsedData parsed, CancellationToken ct)
    {
        foreach (var cargo in parsed.CargoKeys)
        {
            if (!await db.CargoTypes.AnyAsync(x => x.Key == cargo, ct)) db.CargoTypes.Add(new CargoType { Key = cargo, DisplayName = cargo });
        }
        await db.SaveChangesAsync(ct);

        foreach (var companyKey in parsed.CompanyKeys)
        {
            var c = await db.Companies.FirstOrDefaultAsync(x => x.Key == companyKey, ct);
            if (c is null) db.Companies.Add(new Company { Key = companyKey, DisplayName = companyKey, IsUnmapped = false });
            else c.IsUnmapped = false;
        }
        await db.SaveChangesAsync(ct);

        var companies = await db.Companies.ToDictionaryAsync(x => x.Key, ct);
        var cargos = await db.CargoTypes.ToDictionaryAsync(x => x.Key, ct);
        foreach (var rule in parsed.Rules)
        {
            var companyId = companies[rule.CompanyKey].Id;
            var cargoId = cargos[rule.CargoKey].Id;
            var exists = await db.CompanyCargoRules.AnyAsync(x => x.CompanyId == companyId && x.CargoTypeId == cargoId && x.Direction == rule.Direction, ct);
            if (!exists) db.CompanyCargoRules.Add(new CompanyCargoRule { CompanyId = companyId, CargoTypeId = cargoId, Direction = rule.Direction });
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        var unmapped = await db.Companies.Where(x => x.IsUnmapped).ToListAsync(ct);
        var mapped = await db.Companies.Where(x => !x.IsUnmapped).ToListAsync(ct);
        foreach (var u in unmapped)
        {
            var best = mapped
                .Select(m => new { Company = m, Score = Fuzzy.Score(CompanyNormalizer.Simplify(u.Key), CompanyNormalizer.Simplify(m.Key)) })
                .OrderByDescending(x => x.Score).FirstOrDefault();
            if (best is not null && best.Score >= 0.85)
            {
                db.CompanyAliases.Add(new CompanyAlias { AliasKey = u.Key, CompanyId = best.Company.Id, Source = "reconcile" });
                var links = await db.CityCompanies.Where(x => x.CompanyId == u.Id).ToListAsync(ct);
                foreach (var link in links)
                {
                    if (!await db.CityCompanies.AnyAsync(x => x.CityId == link.CityId && x.CompanyId == best.Company.Id, ct))
                        db.CityCompanies.Add(new CityCompany { CityId = link.CityId, CompanyId = best.Company.Id, Source = link.Source });
                    db.CityCompanies.Remove(link);
                }
                db.Companies.Remove(u);
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task<ImportSummary> BuildSummary(CancellationToken ct) =>
        new(await db.Cities.CountAsync(ct), await db.Companies.CountAsync(ct), await db.CityCompanies.CountAsync(ct), await db.CargoTypes.CountAsync(ct), await db.CompanyCargoRules.CountAsync(ct), await db.Companies.CountAsync(x => x.IsUnmapped, ct));
}

public static class Ets2PathDetector
{
    public static IEnumerable<string> GetCandidateRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return @"C:\Program Files (x86)\Steam";
        yield return @"D:\Steam";
        yield return Path.Combine(home, ".steam", "steam");
        yield return Path.Combine(home, ".local", "share", "Steam");
    }

    public static string? TryFindEts2(string steamRoot)
    {
        var libraryVdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(libraryVdf)) return null;
        var libs = File.ReadAllText(libraryVdf)
            .Split('\n')
            .Where(l => l.Contains("path"))
            .Select(l => Regex.Match(l, "\"path\"\\s+\"(?<p>.+?)\"").Groups["p"].Value.Replace("\\\\", "\\"))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct();
        foreach (var lib in libs.Prepend(steamRoot))
        {
            var manifest = Path.Combine(lib, "steamapps", "appmanifest_227300.acf");
            var folder = Path.Combine(lib, "steamapps", "common", "Euro Truck Simulator 2");
            if (File.Exists(manifest) && File.Exists(Path.Combine(folder, "def.scs"))) return folder;
        }
        return null;
    }
}

public class TsMapRunner(ILogger logger, ImportProgress progress)
{
    public async Task<string> EnsureTsMapAsync(CancellationToken ct)
    {
        var toolsDir = Path.Combine(AppContext.BaseDirectory, "tools", "ts-map");
        Directory.CreateDirectory(toolsDir);
        var metaPath = Path.Combine(toolsDir, "meta.json");
        if (File.Exists(metaPath))
        {
            var meta = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(metaPath, ct));
            if (meta is not null && meta.TryGetValue("exe", out var exe) && File.Exists(exe)) return exe;
        }

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Ets2RoutePlanner");
        var json = await http.GetStringAsync("https://api.github.com/repos/ts-map/ts-map/releases/latest", ct);
        using var doc = JsonDocument.Parse(json);
        var assets = doc.RootElement.GetProperty("assets").EnumerateArray();
        var keyword = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" : "mac";
        var asset = assets.Select(a => new { Name = a.GetProperty("name").GetString()!, Url = a.GetProperty("browser_download_url").GetString()! })
            .FirstOrDefault(a => a.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            ?? assets.Select(a => new { Name = a.GetProperty("name").GetString()!, Url = a.GetProperty("browser_download_url").GetString()! }).First();

        var zip = Path.Combine(toolsDir, asset.Name);
        if (!File.Exists(zip))
        {
            progress.Log($"Downloading {asset.Name}");
            await File.WriteAllBytesAsync(zip, await http.GetByteArrayAsync(asset.Url, ct), ct);
        }
        var versionDir = Path.Combine(toolsDir, Path.GetFileNameWithoutExtension(asset.Name));
        if (!Directory.Exists(versionDir)) ZipFile.ExtractToDirectory(zip, versionDir, true);
        var exe = Directory.GetFiles(versionDir, OperatingSystem.IsWindows() ? "*.exe" : "*", SearchOption.AllDirectories)
            .First(f => Path.GetFileName(f).Contains("ts-map", StringComparison.OrdinalIgnoreCase));
        await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(new Dictionary<string, string> { ["exe"] = exe }), ct);
        return exe;
    }

    public async Task<string> RunExportAsync(string tsMapExe, string ets2Path, CancellationToken ct)
    {
        var exportPath = Path.Combine(AppContext.BaseDirectory, "exports", "ts-map", DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
        Directory.CreateDirectory(exportPath);
        var psi = new ProcessStartInfo(tsMapExe)
        {
            WorkingDirectory = Path.GetDirectoryName(tsMapExe)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("export");
        psi.ArgumentList.Add("--game");
        psi.ArgumentList.Add(ets2Path);
        psi.ArgumentList.Add("--output");
        psi.ArgumentList.Add(exportPath);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start ts-map");
        _ = Task.Run(async () => { while (!proc.HasExited) { var l = await proc.StandardOutput.ReadLineAsync(); if (l is not null) progress.Log(l); } }, ct);
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0) throw new InvalidOperationException($"ts-map failed with code {proc.ExitCode}: {await proc.StandardError.ReadToEndAsync(ct)}");
        return exportPath;
    }
}

public record CoordEntity(string Name, double Lat, double Lon);
public record TsMapData(List<CoordEntity> Cities, List<CoordEntity> Depots);
public static class TsMapParser
{
    public static TsMapData Parse(string exportFolder)
    {
        var files = Directory.GetFiles(exportFolder, "*.*", SearchOption.AllDirectories)
            .Where(x => x.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".geojson", StringComparison.OrdinalIgnoreCase)).ToList();
        var cityFile = files.Where(f => Path.GetFileName(f).Contains("city", StringComparison.OrdinalIgnoreCase)).OrderByDescending(f => new FileInfo(f).Length).First();
        var poiFile = files.Where(f => Path.GetFileName(f).Contains("poi", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).Contains("company", StringComparison.OrdinalIgnoreCase)).OrderByDescending(f => new FileInfo(f).Length).First();
        return new TsMapData(ParseFeatureFile(cityFile, false), ParseFeatureFile(poiFile, true));
    }

    private static List<CoordEntity> ParseFeatureFile(string file, bool depotsOnly)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var list = new List<CoordEntity>();
        var features = doc.RootElement.GetProperty("features").EnumerateArray();
        foreach (var f in features)
        {
            var props = f.GetProperty("properties");
            var kind = props.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            if (depotsOnly && !(kind.Contains("company", StringComparison.OrdinalIgnoreCase) || kind.Contains("depot", StringComparison.OrdinalIgnoreCase) || kind.Contains("industry", StringComparison.OrdinalIgnoreCase))) continue;
            if (depotsOnly && (kind.Contains("view", StringComparison.OrdinalIgnoreCase) || kind.Contains("landmark", StringComparison.OrdinalIgnoreCase))) continue;
            var name = props.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) continue;
            var coords = f.GetProperty("geometry").GetProperty("coordinates");
            list.Add(new CoordEntity(name!, coords[1].GetDouble(), coords[0].GetDouble()));
        }
        return list;
    }
}

public record ParsedRule(string CompanyKey, string CargoKey, CargoDirection Direction);
public record Ets2ParsedData(HashSet<string> CargoKeys, HashSet<string> CompanyKeys, List<ParsedRule> Rules);
public static class Ets2DefParser
{
    public static Ets2ParsedData ParseFromInstall(string ets2Path, ImportProgress progress)
    {
        var archives = Directory.GetFiles(ets2Path, "*.scs", SearchOption.TopDirectoryOnly)
            .Where(f => Path.GetFileName(f).Equals("def.scs", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).StartsWith("dlc_", StringComparison.OrdinalIgnoreCase));
        var cargos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var companies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rules = new List<ParsedRule>();
        foreach (var arc in archives)
        {
            progress.Log($"Reading {Path.GetFileName(arc)}");
            using var zip = ZipFile.OpenRead(arc);
            foreach (var e in zip.Entries)
            {
                var p = e.FullName.Replace('\\', '/').ToLowerInvariant();
                if (p.StartsWith("def/cargo/") && p.EndsWith(".sii")) cargos.Add(Path.GetFileNameWithoutExtension(p));
                var m = Regex.Match(p, "def/company/(?<c>[^/]+)/(?<d>in|out)/[^/]+\\.sii$");
                if (m.Success)
                {
                    var company = m.Groups["c"].Value;
                    companies.Add(company);
                    using var sr = new StreamReader(e.Open(), Encoding.UTF8);
                    var text = sr.ReadToEnd();
                    foreach (Match cm in Regex.Matches(text, "cargo(?:es)?\\[\\]\\s*:\\s*([a-z0-9_]+)", RegexOptions.IgnoreCase))
                    {
                        var cargo = cm.Groups[1].Value.ToLowerInvariant();
                        cargos.Add(cargo);
                        rules.Add(new ParsedRule(company, cargo, m.Groups["d"].Value == "in" ? CargoDirection.In : CargoDirection.Out));
                    }
                }
            }
        }
        return new Ets2ParsedData(cargos, companies, rules);
    }
}

public static class CompanyNormalizer
{
    static readonly string[] Suffixes = ["depot", "factory", "warehouse", "storage", "market", "quarry", "site", "plant", "terminal"];
    public static string Normalize(string raw)
    {
        var s = raw.Trim().ToLowerInvariant();
        s = Regex.Replace(s, "[\\p{P}]", " ");
        s = Regex.Replace(s, "\\s+", " ");
        s = s.Replace(" ", "_");
        foreach (var suffix in Suffixes) if (s.EndsWith("_" + suffix)) s = s[..^(suffix.Length + 1)];
        return s.Trim('_');
    }
    public static string Simplify(string s) => Regex.Replace(Normalize(s), "_", "");
}

public static class Fuzzy
{
    public static double Score(string a, string b)
    {
        if (a == b) return 1;
        var lev = Levenshtein(a, b);
        var ratio = 1d - (double)lev / Math.Max(a.Length, b.Length);
        var tokensA = a.Split('_', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var tokensB = b.Split('_', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var overlap = tokensA.Count == 0 ? 0 : tokensA.Intersect(tokensB).Count() / (double)tokensA.Union(tokensB).Count();
        return Math.Max(ratio, overlap);
    }
    private static int Levenshtein(string s, string t)
    {
        var d = new int[s.Length + 1, t.Length + 1];
        for (var i = 0; i <= s.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= t.Length; j++) d[0, j] = j;
        for (var i = 1; i <= s.Length; i++)
            for (var j = 1; j <= t.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (s[i - 1] == t[j - 1] ? 0 : 1));
        return d[s.Length, t.Length];
    }
}

public static class Geo
{
    public static CoordEntity? NearestCity(double lat, double lon, IEnumerable<CoordEntity> cities, double radiusKm)
    {
        CoordEntity? best = null; double bestD = double.MaxValue;
        foreach (var city in cities)
        {
            var d = Haversine(lat, lon, city.Lat, city.Lon);
            if (d < bestD) { bestD = d; best = city; }
        }
        return bestD <= radiusKm ? best : null;
    }
    private static double Haversine(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Asin(Math.Sqrt(a));
    }
}
