namespace Ets2RoutePlanner.Core;

public interface IRecommendationService
{
    Task<SuggestionResult> SuggestAsync(string startCityName, string targetCityName, CancellationToken ct = default);
}
