using SpotCont.Models;

namespace SpotCont.Services;

public sealed class SearchEngine
{
    private readonly SearchPluginHost _pluginHost;
    private readonly UsageHistoryService _usageHistoryService;
    private Task? _initializationTask;

    public SearchEngine(SearchPluginHost pluginHost, UsageHistoryService usageHistoryService)
    {
        _pluginHost = pluginHost;
        _usageHistoryService = usageHistoryService;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        return _initializationTask ??= InitializeCoreAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SearchResultItem>> SearchAsync(
        string? query,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        var request = SearchTextUtility.CreateRequest(query);
        if (request.IsEmpty || maxResults <= 0)
        {
            return Array.Empty<SearchResultItem>();
        }

        var tasks = _pluginHost.Plugins
            .Select(plugin => plugin.SearchAsync(request, Math.Max(maxResults * 3, 30), cancellationToken).AsTask())
            .ToArray();

        var pluginResults = await Task.WhenAll(tasks);
        cancellationToken.ThrowIfCancellationRequested();

        var mergedResults = new Dictionary<string, SearchResultItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var batch in pluginResults)
        {
            foreach (var result in batch)
            {
                var mergeKey = GetMergeKey(result);
                if (!mergedResults.TryGetValue(mergeKey, out var existing) || result.MatchQuality > existing.MatchQuality)
                {
                    mergedResults[mergeKey] = result;
                }
            }
        }

        var orderedResults = mergedResults.Values.ToList();
        ApplyScores(request, orderedResults);

        return orderedResults
            .OrderBy(item => GetBucketPriority(item.Bucket))
            .ThenByDescending(item => item.Score)
            .ThenByDescending(item => item.MatchQuality)
            .ThenBy(item => GetKindPriority(item.Kind))
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .ToArray();
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        var initializationTasks = _pluginHost.Plugins
            .Select(plugin => plugin.InitializeAsync(cancellationToken).AsTask())
            .ToArray();

        await Task.WhenAll(initializationTasks);
    }

    private void ApplyScores(SearchRequest request, IReadOnlyList<SearchResultItem> results)
    {
        var now = DateTimeOffset.UtcNow;
        var maxLaunchCount = 0;
        var learningScores = _usageHistoryService.GetLearningScores(
            request.NormalizedQuery,
            results.Select(result => result.Id).ToArray());

        foreach (var result in results)
        {
            var usage = _usageHistoryService.GetUsage(result.Id);
            if (usage is null)
            {
                continue;
            }

            result.LaunchCount = usage.LaunchCount;
            result.LastLaunchedUtc = usage.LastLaunchedUtc;
            maxLaunchCount = Math.Max(maxLaunchCount, usage.LaunchCount);
        }

        foreach (var result in results)
        {
            result.FrequencyScore = maxLaunchCount <= 0 || result.LaunchCount <= 0
                ? 0
                : Math.Log(result.LaunchCount + 1) / Math.Log(maxLaunchCount + 1);

            result.RecencyScore = result.LastLaunchedUtc is null
                ? 0
                : Math.Exp(-Math.Max(0, (now - result.LastLaunchedUtc.Value).TotalDays) / 30.0);

            result.LearningScore = learningScores.TryGetValue(result.Id, out var learningScore)
                ? learningScore
                : 0;

            result.Score = result.MatchQuality * 0.64 +
                result.FrequencyScore * 0.14 +
                result.RecencyScore * 0.08 +
                result.LearningScore * 0.14;
        }
    }

    private static string GetMergeKey(SearchResultItem item)
    {
        return item.Bucket switch
        {
            SearchResultBucket.Shortcut => $"shortcut|{item.ActionKind}|{item.ActionValue}",
            SearchResultBucket.Alias => $"alias|{item.ActionKind}|{item.ActionValue}",
            SearchResultBucket.Executable => $"executable|{item.ActionKind}|{item.ResolvedTarget}",
            SearchResultBucket.Folder => $"folder|{item.ResolvedTarget}",
            SearchResultBucket.File => $"file|{item.ResolvedTarget}",
            SearchResultBucket.Web => $"web|{item.ActionKind}|{item.ActionValue}",
            _ => $"{item.Bucket}|{item.Id}"
        };
    }

    private static int GetBucketPriority(SearchResultBucket bucket)
    {
        return bucket switch
        {
            SearchResultBucket.Calculator => 0,
            SearchResultBucket.SystemCommand => 1,
            SearchResultBucket.Alias => 2,
            SearchResultBucket.Shortcut => 3,
            SearchResultBucket.Executable => 4,
            SearchResultBucket.Folder => 5,
            SearchResultBucket.File => 6,
            SearchResultBucket.Web => 7,
            _ => 99
        };
    }

    private static int GetKindPriority(SearchResultKind kind)
    {
        return kind switch
        {
            SearchResultKind.Calculator => 0,
            SearchResultKind.SystemCommand => 1,
            SearchResultKind.Command => 2,
            SearchResultKind.Application => 3,
            SearchResultKind.Folder => 4,
            SearchResultKind.File => 5,
            SearchResultKind.Web => 6,
            _ => 99
        };
    }
}
