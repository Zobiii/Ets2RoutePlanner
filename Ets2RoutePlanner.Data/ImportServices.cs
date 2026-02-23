
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Ets2RoutePlanner.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ets2RoutePlanner.Data;

public record ImportSummary(int Cities, int Companies, int CityCompanies, int CargoTypes, int Rules, int UnmappedCompanies);
public record ImportLogChunk(int NextCursor, IReadOnlyList<string> Lines);
public record ImportStatusSnapshot(
    bool IsRunning,
    bool NeedsManualPath,
    string? Error,
    ImportSummary? Summary,
    ImportLogChunk Logs);
public record ImportStartResult(bool Started, string Message);
internal sealed record ImportRequest(string? Ets2Path);

public interface IImportCoordinator
{
    Task<ImportSummary> RunFullImportAsync(string? ets2Path = null, CancellationToken ct = default);
    Task ClearDatabaseAsync(CancellationToken ct = default);
    Task<string?> DetectEts2PathAsync(CancellationToken ct = default);
}

public interface IImportJobService
{
    Task<ImportStartResult> StartFullImportAsync(string? ets2Path = null, CancellationToken ct = default);
    ImportStatusSnapshot GetStatus(int logCursor);
    Task ClearDatabaseAsync(CancellationToken ct = default);
}

public sealed class Ets2PathNotDetectedException : InvalidOperationException
{
    public Ets2PathNotDetectedException()
        : base("Extracted ETS2 path was not auto-detected. Please select your extracted ETS2 folder.")
    {
    }
}

public sealed class ImportProgress
{
    private readonly object _gate = new();
    private readonly List<string> _logs = [];

    public void Reset()
    {
        lock (_gate)
        {
            _logs.Clear();
        }
    }

    public void Log(string message)
    {
        lock (_gate)
        {
            _logs.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        }
    }

    public ImportLogChunk GetChunk(int cursor)
    {
        lock (_gate)
        {
            var safeCursor = Math.Clamp(cursor, 0, _logs.Count);
            var lines = _logs.Skip(safeCursor).ToArray();
            return new ImportLogChunk(_logs.Count, lines);
        }
    }
}

public sealed class ImportCoordinator(
    AppDbContext db,
    ILogger<ImportCoordinator> logger,
    ImportProgress progress) : IImportCoordinator
{
    public Task<string?> DetectEts2PathAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Ets2PathDetector.TryFindInstalledPath());
    }

    public async Task<ImportSummary> RunFullImportAsync(string? ets2Path = null, CancellationToken ct = default)
    {
        await DatabaseSchemaBootstrapper.EnsureSchemaAsync(db, ct);

        ImportLog? logRow = null;
        try
        {
            logRow = new ImportLog
            {
                Kind = "full",
                StartedAtUtc = DateTime.UtcNow,
                Success = false,
                Message = string.Empty
            };

            db.ImportLogs.Add(logRow);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            var baseMsg = ex.GetBaseException().Message;
            logger.LogWarning(ex, "ImportLog pre-write failed; continuing without ImportLog row.");
            progress.Log($"WARN: Could not write ImportLog row ({baseMsg}). Continuing import.");
            if (logRow is not null)
            {
                db.Entry(logRow).State = EntityState.Detached;
                logRow = null;
            }
        }

        try
        {
            progress.Log("Detect ETS2 path");
            ets2Path = ResolveEts2Path(ets2Path, await DetectEts2PathAsync(ct));

            var hasExtractedDef = Directory.Exists(Path.Combine(ets2Path, "def"));
            if (!hasExtractedDef)
            {
                throw new InvalidOperationException(
                    $"Invalid extracted ETS2 path. Missing required 'def' folder in '{ets2Path}'.");
            }

            progress.Log("Parse extracted city/company definitions");
            var mapData = Ets2CompanyCityParser.ParseFromExtractedFolder(ets2Path, progress);
            await UpsertCitiesAndCompaniesFromDefsAsync(mapData, ct);

            progress.Log("Parse cargo/company definitions and build rules");
            var parsed = Ets2DefParser.ParseFromInstall(ets2Path, progress);
            await UpsertCargoAndRulesAsync(parsed, ct);

            progress.Log("Reconcile companies");
            await ReconcileAsync(ct);

            var summary = await BuildSummary(ct);
            if (logRow is not null)
            {
                logRow.Success = true;
                logRow.Message = JsonSerializer.Serialize(summary);
                logRow.EndedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }

            progress.Log("Import complete");
            return summary;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Full import failed");
            if (logRow is not null)
            {
                logRow.Success = false;
                logRow.Message = ex.ToString();
                logRow.EndedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            throw;
        }
    }

    public async Task ClearDatabaseAsync(CancellationToken ct = default)
    {
        progress.Log("Clearing SQLite database");
        await db.Database.EnsureDeletedAsync(ct);

        var connString = db.Database.GetConnectionString();
        if (!string.IsNullOrWhiteSpace(connString))
        {
            var sqlite = new SqliteConnectionStringBuilder(connString);
            if (!string.IsNullOrWhiteSpace(sqlite.DataSource))
            {
                var dbFile = sqlite.DataSource;
                if (!Path.IsPathRooted(dbFile))
                {
                    dbFile = Path.Combine(AppContext.BaseDirectory, dbFile);
                }

                if (File.Exists(dbFile))
                {
                    File.Delete(dbFile);
                }
            }
        }

        await db.Database.MigrateAsync(ct);
        progress.Log("Database cleared");
    }

    private static string ResolveEts2Path(string? requestedPath, string? detectedPath)
    {
        if (!string.IsNullOrWhiteSpace(requestedPath))
        {
            return requestedPath;
        }

        if (!string.IsNullOrWhiteSpace(detectedPath))
        {
            return detectedPath;
        }

        throw new Ets2PathNotDetectedException();
    }

    private async Task UpsertCitiesAndCompaniesFromDefsAsync(Ets2CompanyCityData mapData, CancellationToken ct)
    {
        var cityLookup = await db.Cities
            .ToDictionaryAsync(c => c.Name.Trim().ToLowerInvariant(), ct);

        foreach (var cityName in mapData.Cities)
        {
            var key = cityName.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(key) || cityLookup.ContainsKey(key))
            {
                continue;
            }

            var entity = new City { Name = cityName.Trim() };
            db.Cities.Add(entity);
            cityLookup[key] = entity;
        }

        await db.SaveChangesAsync(ct);

        var companyByKey = await db.Companies.ToDictionaryAsync(c => c.Key, ct);
        foreach (var companyKey in mapData.Links.Select(x => x.CompanyKey).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (companyByKey.ContainsKey(companyKey))
            {
                continue;
            }

            var company = new Company
            {
                Key = companyKey,
                DisplayName = companyKey,
                IsUnmapped = false
            };

            db.Companies.Add(company);
            companyByKey[companyKey] = company;
        }

        await db.SaveChangesAsync(ct);

        var refreshedCities = await db.Cities
            .ToDictionaryAsync(c => c.Name.Trim().ToLowerInvariant(), ct);
        companyByKey = await db.Companies.ToDictionaryAsync(c => c.Key, ct);

        var existingLinks = await db.CityCompanies
            .Select(x => new { x.CityId, x.CompanyId })
            .ToListAsync(ct);
        var cityCompanySet = existingLinks
            .Select(x => (x.CityId, x.CompanyId))
            .ToHashSet();

        foreach (var link in mapData.Links)
        {
            var cityKey = link.CityName.Trim().ToLowerInvariant();
            if (!refreshedCities.TryGetValue(cityKey, out var cityEntity))
            {
                continue;
            }

            if (!companyByKey.TryGetValue(link.CompanyKey, out var company))
            {
                continue;
            }

            if (!cityCompanySet.Add((cityEntity.Id, company.Id)))
            {
                continue;
            }

            db.CityCompanies.Add(new CityCompany
            {
                CityId = cityEntity.Id,
                CompanyId = company.Id,
                Source = CityCompanySource.TsMap
            });
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task UpsertCargoAndRulesAsync(Ets2ParsedData parsed, CancellationToken ct)
    {
        var cargoByKey = await db.CargoTypes.ToDictionaryAsync(x => x.Key, ct);

        foreach (var cargo in parsed.CargoKeys)
        {
            if (!cargoByKey.ContainsKey(cargo))
            {
                var entity = new CargoType { Key = cargo, DisplayName = cargo };
                db.CargoTypes.Add(entity);
                cargoByKey[cargo] = entity;
            }
        }

        await db.SaveChangesAsync(ct);
        cargoByKey = await db.CargoTypes.ToDictionaryAsync(x => x.Key, ct);

        var companyByKey = await db.Companies.ToDictionaryAsync(x => x.Key, ct);

        foreach (var companyKey in parsed.CompanyKeys)
        {
            if (!companyByKey.TryGetValue(companyKey, out var company))
            {
                company = new Company
                {
                    Key = companyKey,
                    DisplayName = companyKey,
                    IsUnmapped = false
                };

                db.Companies.Add(company);
                companyByKey[companyKey] = company;
            }
            else
            {
                company.IsUnmapped = false;
                if (string.IsNullOrWhiteSpace(company.DisplayName))
                {
                    company.DisplayName = companyKey;
                }
            }
        }

        await db.SaveChangesAsync(ct);

        companyByKey = await db.Companies.ToDictionaryAsync(x => x.Key, ct);

        var existingRules = await db.CompanyCargoRules
            .Select(x => new { x.CompanyId, x.CargoTypeId, x.Direction })
            .ToListAsync(ct);

        var ruleSet = existingRules
            .Select(x => (x.CompanyId, x.CargoTypeId, x.Direction))
            .ToHashSet();

        foreach (var rule in parsed.Rules)
        {
            if (!companyByKey.TryGetValue(rule.CompanyKey, out var company) ||
                !cargoByKey.TryGetValue(rule.CargoKey, out var cargo))
            {
                continue;
            }

            if (ruleSet.Add((company.Id, cargo.Id, rule.Direction)))
            {
                db.CompanyCargoRules.Add(new CompanyCargoRule
                {
                    CompanyId = company.Id,
                    CargoTypeId = cargo.Id,
                    Direction = rule.Direction
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        var internalCompanies = await db.Companies
            .Where(c => !c.IsUnmapped)
            .OrderBy(c => c.Key)
            .ToListAsync(ct);

        var unmappedCompanies = await db.Companies
            .Where(c => c.IsUnmapped)
            .OrderBy(c => c.Key)
            .ToListAsync(ct);

        foreach (var unmapped in unmappedCompanies)
        {
            var sourceNorm = CompanyNormalizer.Simplify(unmapped.Key);
            var candidates = internalCompanies
                .Select(c =>
                {
                    var targetNorm = CompanyNormalizer.Simplify(c.Key);
                    var score = Fuzzy.Score(sourceNorm, targetNorm);
                    var distance = Fuzzy.LevenshteinDistance(sourceNorm, targetNorm);
                    var overlap = Fuzzy.TokenOverlap(sourceNorm, targetNorm);
                    return new { Company = c, Score = score, Distance = distance, Overlap = overlap };
                })
                .OrderByDescending(c => c.Score)
                .ThenBy(c => c.Distance)
                .ToList();

            var best = candidates.FirstOrDefault();
            if (best is null)
            {
                continue;
            }

            var maxLen = Math.Max(sourceNorm.Length, CompanyNormalizer.Simplify(best.Company.Key).Length);
            var levThreshold = Math.Max(2, (int)Math.Ceiling(maxLen * 0.15));
            var canMerge = best.Score >= 0.85 || best.Distance <= levThreshold || best.Overlap >= 0.6;

            if (!canMerge)
            {
                continue;
            }

            await MergeCompanyIntoAsync(unmapped.Id, best.Company.Id, "reconcile", ct);
            progress.Log($"Reconciled alias '{unmapped.Key}' -> '{best.Company.Key}'");
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task MergeCompanyIntoAsync(int sourceCompanyId, int targetCompanyId, string sourceTag, CancellationToken ct)
    {
        if (sourceCompanyId == targetCompanyId)
        {
            return;
        }

        var sourceCompany = await db.Companies.FirstOrDefaultAsync(c => c.Id == sourceCompanyId, ct);
        if (sourceCompany is null)
        {
            return;
        }

        var aliases = await db.CompanyAliases
            .Where(a => a.CompanyId == sourceCompanyId)
            .ToListAsync(ct);

        foreach (var alias in aliases)
        {
            alias.CompanyId = targetCompanyId;
            alias.Source = sourceTag;
        }

        var cityLinks = await db.CityCompanies
            .Where(x => x.CompanyId == sourceCompanyId)
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
                    Source = link.Source
                });
            }

            db.CityCompanies.Remove(link);
        }

        var hasRules = await db.CompanyCargoRules.AnyAsync(r => r.CompanyId == sourceCompanyId, ct);
        if (!hasRules)
        {
            db.Companies.Remove(sourceCompany);
        }
    }

    private async Task<ImportSummary> BuildSummary(CancellationToken ct)
    {
        return new ImportSummary(
            await db.Cities.CountAsync(ct),
            await db.Companies.CountAsync(ct),
            await db.CityCompanies.CountAsync(ct),
            await db.CargoTypes.CountAsync(ct),
            await db.CompanyCargoRules.CountAsync(ct),
            await db.Companies.CountAsync(c => c.IsUnmapped, ct));
    }
}
public sealed class ImportHostedService(
    IServiceScopeFactory scopeFactory,
    ImportProgress progress,
    ILogger<ImportHostedService> logger) : BackgroundService, IImportJobService
{
    private readonly Channel<ImportRequest> _queue = Channel.CreateUnbounded<ImportRequest>();
    private readonly object _gate = new();
    private bool _isRunning;
    private bool _needsManualPath;
    private string? _error;
    private ImportSummary? _summary;

    public Task<ImportStartResult> StartFullImportAsync(string? ets2Path = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_isRunning)
            {
                return Task.FromResult(new ImportStartResult(false, "Import is already running."));
            }

            _isRunning = true;
            _needsManualPath = false;
            _error = null;
            _summary = null;
            progress.Reset();
            progress.Log("Queued full import");
            _queue.Writer.TryWrite(new ImportRequest(ets2Path));
            return Task.FromResult(new ImportStartResult(true, "Import started."));
        }
    }

    public ImportStatusSnapshot GetStatus(int logCursor)
    {
        lock (_gate)
        {
            return new ImportStatusSnapshot(
                _isRunning,
                _needsManualPath,
                _error,
                _summary,
                progress.GetChunk(logCursor));
        }
    }

    public async Task ClearDatabaseAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_isRunning)
            {
                throw new InvalidOperationException("Cannot clear DB while import is running.");
            }

            _error = null;
            _summary = null;
            _needsManualPath = false;
            progress.Reset();
            progress.Log("Clear DB requested");
        }

        using var scope = scopeFactory.CreateScope();
        var coordinator = scope.ServiceProvider.GetRequiredService<IImportCoordinator>();
        await coordinator.ClearDatabaseAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var coordinator = scope.ServiceProvider.GetRequiredService<IImportCoordinator>();
                var result = await coordinator.RunFullImportAsync(request.Ets2Path, stoppingToken);

                lock (_gate)
                {
                    _summary = result;
                    _error = null;
                    _needsManualPath = false;
                }
            }
            catch (Ets2PathNotDetectedException ex)
            {
                progress.Log(ex.Message);
                lock (_gate)
                {
                    _needsManualPath = true;
                    _error = ex.Message;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Background import failed");
                progress.Log($"ERROR: {ex.Message}");

                lock (_gate)
                {
                    _error = ex.Message;
                    _needsManualPath = false;
                }
            }
            finally
            {
                lock (_gate)
                {
                    _isRunning = false;
                }
            }
        }
    }
}

public static class Ets2PathDetector
{
    public static string? TryFindInstalledPath()
    {
        var candidateSteamRoots = GetCandidateSteamRoots().Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var steamRoot in candidateSteamRoots)
        {
            var found = TryFindFromSteamRoot(steamRoot);
            if (found is not null)
            {
                return found;
            }
        }

        foreach (var directPath in GetDirectInstallCandidates().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(Path.Combine(directPath, "def")))
            {
                return directPath;
            }
        }

        return null;
    }

    private static IEnumerable<string> GetCandidateSteamRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam");
        yield return Path.Combine(home, ".steam", "steam");
        yield return Path.Combine(home, ".local", "share", "Steam");

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            yield return Path.Combine(drive.RootDirectory.FullName, "Steam");
            yield return Path.Combine(drive.RootDirectory.FullName, "Program Files (x86)", "Steam");
            yield return Path.Combine(drive.RootDirectory.FullName, "Program Files", "Steam");
        }
    }

    private static string? TryFindFromSteamRoot(string steamRoot)
    {
        if (!Directory.Exists(steamRoot))
        {
            return null;
        }

        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steamRoot };
        var libraryVdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");

        if (File.Exists(libraryVdf))
        {
            var text = File.ReadAllText(libraryVdf);
            foreach (Match match in Regex.Matches(text, "\\\"path\\\"\\s*\\\"(?<p>.*?)\\\"", RegexOptions.IgnoreCase))
            {
                var raw = match.Groups["p"].Value;
                var unescaped = raw.Replace("\\\\", "\\").Trim();
                if (!string.IsNullOrWhiteSpace(unescaped))
                {
                    libraries.Add(unescaped);
                }
            }
        }

        foreach (var lib in libraries)
        {
            var manifest = Path.Combine(lib, "steamapps", "appmanifest_227300.acf");
            if (!File.Exists(manifest))
            {
                continue;
            }

            var installDir = "Euro Truck Simulator 2";
            var manifestText = File.ReadAllText(manifest);
            var dirMatch = Regex.Match(manifestText, "\\\"installdir\\\"\\s*\\\"(?<d>.*?)\\\"", RegexOptions.IgnoreCase);
            if (dirMatch.Success)
            {
                installDir = dirMatch.Groups["d"].Value;
            }

            var gamePath = Path.Combine(lib, "steamapps", "common", installDir);
            if (Directory.Exists(Path.Combine(gamePath, "def")))
            {
                return gamePath;
            }
        }

        return null;
    }

    private static IEnumerable<string> GetDirectInstallCandidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            yield return Path.Combine(drive.RootDirectory.FullName, "SteamLibrary", "steamapps", "common", "Euro Truck Simulator 2");
            yield return Path.Combine(drive.RootDirectory.FullName, "Games", "SteamLibrary", "steamapps", "common", "Euro Truck Simulator 2");
        }

        yield return Path.Combine(home, ".steam", "steam", "steamapps", "common", "Euro Truck Simulator 2");
        yield return Path.Combine(home, ".local", "share", "Steam", "steamapps", "common", "Euro Truck Simulator 2");
    }
}
public sealed record Ets2CompanyCityLink(string CompanyKey, string CityName);
public sealed record Ets2CompanyCityData(List<string> Cities, List<Ets2CompanyCityLink> Links);

public static class Ets2CompanyCityParser
{
    public static Ets2CompanyCityData ParseFromExtractedFolder(string extractedRoot, ImportProgress progress)
    {
        var defRoot = Path.Combine(extractedRoot, "def");
        var companyRoot = Path.Combine(defRoot, "company");
        if (!Directory.Exists(companyRoot))
        {
            throw new InvalidOperationException($"Missing required folder: '{companyRoot}'.");
        }

        var citySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var linkSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var links = new List<Ets2CompanyCityLink>();

        foreach (var companyDir in Directory.EnumerateDirectories(companyRoot))
        {
            var companyKey = Path.GetFileName(companyDir)?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(companyKey))
            {
                continue;
            }

            var editorDir = Path.Combine(companyDir, "editor");
            if (!Directory.Exists(editorDir))
            {
                continue;
            }

            foreach (var editorFile in Directory.EnumerateFiles(editorDir, "*.sii", SearchOption.AllDirectories))
            {
                var rawCityToken = Path.GetFileNameWithoutExtension(editorFile);
                if (string.IsNullOrWhiteSpace(rawCityToken))
                {
                    continue;
                }

                var splitIndex = rawCityToken.IndexOf('.');
                var cityToken = splitIndex > 0 ? rawCityToken[..splitIndex] : rawCityToken;
                var cityName = NormalizeCityName(cityToken);
                if (string.IsNullOrWhiteSpace(cityName))
                {
                    continue;
                }

                citySet.Add(cityName);
                var dedupeKey = $"{companyKey}|{cityName}";
                if (!linkSet.Add(dedupeKey))
                {
                    continue;
                }

                links.Add(new Ets2CompanyCityLink(companyKey, cityName));
            }
        }

        var cityDefRoot = Path.Combine(defRoot, "city");
        if (Directory.Exists(cityDefRoot))
        {
            foreach (var cityFile in Directory.EnumerateFiles(cityDefRoot, "*.sii", SearchOption.AllDirectories))
            {
                var rawCityToken = Path.GetFileNameWithoutExtension(cityFile);
                if (string.IsNullOrWhiteSpace(rawCityToken))
                {
                    continue;
                }

                var splitIndex = rawCityToken.IndexOf('.');
                var cityToken = splitIndex > 0 ? rawCityToken[..splitIndex] : rawCityToken;
                var cityName = NormalizeCityName(cityToken);
                if (!string.IsNullOrWhiteSpace(cityName))
                {
                    citySet.Add(cityName);
                }
            }
        }

        var cities = citySet.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        progress.Log($"Parsed extracted defs: cities={cities.Count}, company-city-links={links.Count}");
        return new Ets2CompanyCityData(cities, links);
    }

    private static string NormalizeCityName(string value)
    {
        var city = value.Trim().ToLowerInvariant().Replace('_', ' ');
        city = Regex.Replace(city, "\\s+", " ").Trim();
        return city;
    }
}
public sealed record ParsedRule(string CompanyKey, string CargoKey, CargoDirection Direction);
public sealed record Ets2ParsedData(HashSet<string> CargoKeys, HashSet<string> CompanyKeys, List<ParsedRule> Rules);

public static class Ets2DefParser
{
    private static readonly Regex CompanyRulePathRegex =
        new("^def/company/(?<company>[^/]+)/(?<direction>in|out)/[^/]+\\.sii$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CargoLineRegex =
        new("(?im)^\\s*cargo(?:es)?(?:\\[\\])?\\s*:\\s*\"?(?<cargo>[a-z0-9_\\.]+)\"?", RegexOptions.Compiled);

    public static Ets2ParsedData ParseFromInstall(string ets2Path, ImportProgress progress)
    {
        var extractedDefRoot = Path.Combine(ets2Path, "def");
        if (Directory.Exists(extractedDefRoot))
        {
            progress.Log("Reading extracted def folder");
            return ParseFromExtractedFolder(ets2Path, progress);
        }

        throw new InvalidOperationException(
            $"Missing extracted 'def' folder in '{ets2Path}'. This importer now expects an already extracted ETS2 source.");
    }

    private static Ets2ParsedData ParseFromExtractedFolder(string ets2Path, ImportProgress progress)
    {
        var defRoot = Path.Combine(ets2Path, "def");
        if (!Directory.Exists(defRoot))
        {
            throw new InvalidOperationException($"Missing extracted def folder: '{defRoot}'.");
        }

        var cargoKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var companyKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rules = new List<ParsedRule>();

        var cargoRoot = Path.Combine(defRoot, "cargo");
        if (Directory.Exists(cargoRoot))
        {
            foreach (var cargoFile in Directory.EnumerateFiles(cargoRoot, "*.sii", SearchOption.AllDirectories))
            {
                var relativeCargoPath = Path.GetRelativePath(defRoot, cargoFile)
                    .Replace('\\', '/')
                    .TrimStart('/')
                    .ToLowerInvariant();
                var cargoKey = TryGetCargoKeyFromDefCargoPath($"def/{relativeCargoPath}");
                if (!string.IsNullOrWhiteSpace(cargoKey))
                {
                    cargoKeys.Add(cargoKey);
                }
            }
        }

        var companyRoot = Path.Combine(defRoot, "company");
        if (Directory.Exists(companyRoot))
        {
            foreach (var companyFile in Directory.EnumerateFiles(companyRoot, "*.sii", SearchOption.AllDirectories))
            {
                string content;
                try
                {
                    content = File.ReadAllText(companyFile, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    progress.Log($"WARN: Failed reading {companyFile}: {ex.Message}");
                    continue;
                }

                var relative = Path.GetRelativePath(ets2Path, companyFile)
                    .Replace('\\', '/')
                    .TrimStart('/')
                    .ToLowerInvariant();

                var ruleMatch = CompanyRulePathRegex.Match(relative);
                if (!ruleMatch.Success)
                {
                    continue;
                }

                var company = ruleMatch.Groups["company"].Value;
                var direction = ruleMatch.Groups["direction"].Value.Equals("in", StringComparison.OrdinalIgnoreCase)
                    ? CargoDirection.In
                    : CargoDirection.Out;

                companyKeys.Add(company);

                foreach (Match cargoMatch in CargoLineRegex.Matches(content))
                {
                    var cargoKey = NormalizeCargoKey(cargoMatch.Groups["cargo"].Value);
                    if (string.IsNullOrWhiteSpace(cargoKey))
                    {
                        continue;
                    }

                    cargoKeys.Add(cargoKey);
                    rules.Add(new ParsedRule(company, cargoKey, direction));
                }
            }
        }

        return new Ets2ParsedData(cargoKeys, companyKeys, rules);
    }

    private static string NormalizeCargoKey(string rawCargo)
    {
        var value = rawCargo.Trim().ToLowerInvariant();
        if (value.Contains('.'))
        {
            value = value[(value.LastIndexOf('.') + 1)..];
        }

        return value;
    }

    private static string? TryGetCargoKeyFromDefCargoPath(string normalizedPath)
    {
        const string prefix = "def/cargo/";
        if (!normalizedPath.StartsWith(prefix, StringComparison.Ordinal)
            || !normalizedPath.EndsWith(".sii", StringComparison.Ordinal))
        {
            return null;
        }

        var rest = normalizedPath[prefix.Length..];
        if (string.IsNullOrWhiteSpace(rest))
        {
            return null;
        }

        var slashIndex = rest.IndexOf('/');
        if (slashIndex > 0)
        {
            return rest[..slashIndex].Trim();
        }

        return Path.GetFileNameWithoutExtension(rest).Trim();
    }
}

public static class CompanyNormalizer
{
    private static readonly string[] Suffixes =
    [
        "depot",
        "factory",
        "warehouse",
        "storage",
        "market",
        "quarry",
        "site",
        "plant",
        "terminal"
    ];

    public static string Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var text = raw.Trim().ToLowerInvariant();
        text = Regex.Replace(text, "[^a-z0-9\\s_]", " ");
        text = Regex.Replace(text, "\\s+", " ");
        text = text.Replace(' ', '_');
        text = Regex.Replace(text, "_+", "_").Trim('_');

        foreach (var suffix in Suffixes)
        {
            var token = "_" + suffix;
            if (text.EndsWith(token, StringComparison.Ordinal))
            {
                text = text[..^token.Length].TrimEnd('_');
            }
            else if (text == suffix)
            {
                text = string.Empty;
            }
        }

        return text;
    }

    public static string Simplify(string raw)
    {
        return Normalize(raw).Replace("_", string.Empty, StringComparison.Ordinal);
    }
}

public static class Fuzzy
{
    public static double Score(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            return 1d;
        }

        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return 0d;
        }

        left = left.ToLowerInvariant();
        right = right.ToLowerInvariant();

        var ratio = 1d - (double)LevenshteinDistance(left, right) / Math.Max(left.Length, right.Length);
        var overlap = TokenOverlap(left, right);

        return Math.Max(ratio, overlap);
    }

    public static int LevenshteinDistance(string left, string right)
    {
        if (left.Length == 0)
        {
            return right.Length;
        }

        if (right.Length == 0)
        {
            return left.Length;
        }

        var matrix = new int[left.Length + 1, right.Length + 1];

        for (var i = 0; i <= left.Length; i++)
        {
            matrix[i, 0] = i;
        }

        for (var j = 0; j <= right.Length; j++)
        {
            matrix[0, j] = j;
        }

        for (var i = 1; i <= left.Length; i++)
        {
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                matrix[i, j] = Math.Min(
                    Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1),
                    matrix[i - 1, j - 1] + cost);
            }
        }

        return matrix[left.Length, right.Length];
    }

    public static double TokenOverlap(string left, string right)
    {
        var leftTokens = SplitTokens(left);
        var rightTokens = SplitTokens(right);

        if (leftTokens.Count == 0 || rightTokens.Count == 0)
        {
            return 0d;
        }

        var intersection = leftTokens.Intersect(rightTokens, StringComparer.Ordinal).Count();
        var union = leftTokens.Union(rightTokens, StringComparer.Ordinal).Count();

        return union == 0 ? 0d : intersection / (double)union;
    }

    private static HashSet<string> SplitTokens(string value)
    {
        var normalized = Regex.Replace(value, "([a-z])([A-Z])", "$1_$2").ToLowerInvariant();
        normalized = Regex.Replace(normalized, "[^a-z0-9]+", "_");

        return normalized.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
    }
}

