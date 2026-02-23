namespace Ets2RoutePlanner.Core;

public enum CargoDirection { In = 0, Out = 1 }
public enum CityCompanySource { TsMap = 0, Manual = 1 }

public class City { public int Id { get; set; } public string Name { get; set; } = string.Empty; }
public class Company
{
    public int Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public bool IsUnmapped { get; set; }
}
public class CompanyAlias
{
    public int Id { get; set; }
    public string AliasKey { get; set; } = string.Empty;
    public int CompanyId { get; set; }
    public string Source { get; set; } = string.Empty;
    public Company? Company { get; set; }
}
public class CargoType { public int Id { get; set; } public string Key { get; set; } = string.Empty; public string? DisplayName { get; set; } }
public class CompanyCargoRule
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int CargoTypeId { get; set; }
    public CargoDirection Direction { get; set; }
    public Company? Company { get; set; }
    public CargoType? CargoType { get; set; }
}
public class CityCompany
{
    public int Id { get; set; }
    public int CityId { get; set; }
    public int CompanyId { get; set; }
    public CityCompanySource Source { get; set; }
    public City? City { get; set; }
    public Company? Company { get; set; }
}
public class ImportLog
{
    public int Id { get; set; }
    public string Kind { get; set; } = string.Empty;
    public DateTime StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

public record RouteSuggestion(string StartCompany, string CargoType, string TargetCompany);
public record SuggestionResult(IReadOnlyList<RouteSuggestion> Suggestions, IReadOnlyList<string> StartCityHints, IReadOnlyList<string> TargetCityHints);
