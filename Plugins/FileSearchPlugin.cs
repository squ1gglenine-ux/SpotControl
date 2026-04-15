using SpotCont.Models;
using SpotCont.Services;

namespace SpotCont.Plugins;

public sealed class FileSearchPlugin : SearchPluginBase
{
    private readonly FileIndexService _fileIndexService;

    public FileSearchPlugin(FileIndexService fileIndexService)
        : base("files", "Files", 40, "Searches indexed folders and files.")
    {
        _fileIndexService = fileIndexService;
    }

    public override ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        _fileIndexService.StartBackgroundIndexing();
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

        var snapshot = _fileIndexService.GetSnapshot();
        if (snapshot.Count == 0)
        {
            return ValueTask.FromResult<IReadOnlyList<SearchResultItem>>(Array.Empty<SearchResultItem>());
        }

        if (request.IsEmpty)
        {
            var suggestions = snapshot
                .OrderBy(item => item.Kind == SearchResultKind.Folder ? 0 : 1)
                .ThenByDescending(item => item.LastModifiedUtc ?? DateTimeOffset.MinValue)
                .Take(maxResults)
                .Select(item => CreateResult(item, item.Kind == SearchResultKind.Folder ? 0.44 : 0.36))
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

            if (!SearchTextUtility.TryGetMatchQuality(item, request, out var matchQuality) || matchQuality < 0.42)
            {
                continue;
            }

            results.Add(CreateResult(item, matchQuality));
        }

        return ValueTask.FromResult<IReadOnlyList<SearchResultItem>>(results
            .OrderByDescending(item => item.MatchQuality)
            .ThenBy(item => item.Kind == SearchResultKind.Folder ? 0 : 1)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .ToArray());
    }

    private static SearchResultItem CreateResult(IndexedItem item, double matchQuality)
    {
        return SearchResultFactory.FromIndexedItem(item, matchQuality);
    }
}
