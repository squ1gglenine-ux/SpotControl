using SpotCont.Models;
using SpotCont.Services;

namespace SpotCont.Plugins;

public sealed class ApplicationSearchPlugin : SearchPluginBase
{
    private readonly ApplicationIndexService _applicationIndexService;

    public ApplicationSearchPlugin(ApplicationIndexService applicationIndexService)
        : base("applications", "Applications", 30, "Searches shortcuts and installed executables.")
    {
        _applicationIndexService = applicationIndexService;
    }

    public override ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        _applicationIndexService.StartBackgroundIndexing();
        return ValueTask.CompletedTask;
    }

    public override ValueTask<IReadOnlyList<SearchResultItem>> SearchAsync(
        SearchRequest request,
        int maxResults,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult<IReadOnlyList<SearchResultItem>>(Array.Empty<SearchResultItem>());
        }

        var snapshot = _applicationIndexService.GetSnapshot();
        if (snapshot.Count == 0)
        {
            return ValueTask.FromResult<IReadOnlyList<SearchResultItem>>(Array.Empty<SearchResultItem>());
        }

        if (request.IsEmpty)
        {
            var suggestions = snapshot
                .Take(maxResults)
                .Select(item => CreateResult(item, 0.5))
                .ToArray();

            return ValueTask.FromResult<IReadOnlyList<SearchResultItem>>(suggestions);
        }

        var results = new List<SearchResultItem>();
        var index = 0;
        foreach (var item in snapshot)
        {
            index++;
            if ((index & 63) == 0 && cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromResult<IReadOnlyList<SearchResultItem>>(Array.Empty<SearchResultItem>());
            }

            if (!SearchTextUtility.TryGetMatchQuality(item, request, out var matchQuality) || matchQuality < 0.44)
            {
                continue;
            }

            results.Add(CreateResult(item, matchQuality));
        }

        return ValueTask.FromResult<IReadOnlyList<SearchResultItem>>(results
            .OrderByDescending(item => item.MatchQuality)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .ToArray());
    }

    private static SearchResultItem CreateResult(IndexedItem item, double matchQuality)
    {
        return SearchResultFactory.FromIndexedItem(item, matchQuality);
    }
}
