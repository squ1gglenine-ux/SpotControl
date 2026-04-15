using SpotCont.Models;
using SpotCont.Services;

namespace SpotCont.Plugins;

public sealed class AutocompleteSearchPlugin : SearchPluginBase
{
    private static readonly CommandSuggestion[] CommandSuggestions =
    {
        new("shutdown", "Shut down Windows", "Turn off the device immediately.", ResultActionKind.Shutdown),
        new("restart", "Restart Windows", "Restart the device immediately.", ResultActionKind.Restart),
        new("lock", "Lock Windows session", "Lock the current account instantly.", ResultActionKind.Lock)
    };

    private static readonly WebsiteSuggestion[] WebsiteSuggestions =
    {
        new("GitHub", "https://github.com"),
        new("Google", "https://www.google.com"),
        new("YouTube", "https://www.youtube.com"),
        new("Stack Overflow", "https://stackoverflow.com"),
        new("ChatGPT", "https://chatgpt.com"),
        new("OpenAI", "https://openai.com"),
        new("Wikipedia", "https://www.wikipedia.org")
    };

    private readonly ApplicationIndexService _applicationIndexService;
    private readonly FileIndexService _fileIndexService;

    public AutocompleteSearchPlugin(
        ApplicationIndexService applicationIndexService,
        FileIndexService fileIndexService)
        : base("autocomplete", "Autocomplete", 10, "Provides the strongest inline completion candidate.")
    {
        _applicationIndexService = applicationIndexService;
        _fileIndexService = fileIndexService;
    }

    public override ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask<IReadOnlyList<SearchResultItem>> SearchAsync(
        SearchRequest request,
        int maxResults,
        CancellationToken cancellationToken)
    {
        if (request.IsEmpty || maxResults <= 0 || cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult<IReadOnlyList<SearchResultItem>>(Array.Empty<SearchResultItem>());
        }

        SearchResultItem? bestResult = null;
        var bestScore = 0d;
        var index = 0;

        foreach (var item in _applicationIndexService.GetSnapshot())
        {
            index++;
            if ((index & 31) == 0 && cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromResult<IReadOnlyList<SearchResultItem>>(Array.Empty<SearchResultItem>());
            }

            if (!TryGetAutocompleteScore(item.AutocompleteText, request, out var score))
            {
                continue;
            }

            if (score <= bestScore)
            {
                continue;
            }

            bestScore = score;
            bestResult = SearchResultFactory.FromIndexedItem(item, score);
        }

        index = 0;
        foreach (var item in _fileIndexService.GetSnapshot().Take(900))
        {
            index++;
            if ((index & 31) == 0 && cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromResult<IReadOnlyList<SearchResultItem>>(Array.Empty<SearchResultItem>());
            }

            if (!TryGetAutocompleteScore(item.AutocompleteText, request, out var score))
            {
                continue;
            }

            if (score <= bestScore)
            {
                continue;
            }

            bestScore = score;
            bestResult = SearchResultFactory.FromIndexedItem(item, score);
        }

        foreach (var command in CommandSuggestions)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromResult<IReadOnlyList<SearchResultItem>>(Array.Empty<SearchResultItem>());
            }

            if (!TryGetAutocompleteScore(command.Id, request, out var score))
            {
                continue;
            }

            if (score <= bestScore)
            {
                continue;
            }

            bestScore = score;
            bestResult = new SearchResultItem
            {
                Id = $"command::{command.Id}",
                Title = command.Title,
                Subtitle = command.Subtitle,
                Target = command.Id,
                Kind = SearchResultKind.SystemCommand,
                ActionKind = command.ActionKind,
                ActionValue = command.Id,
                AutocompleteText = command.Id,
                MatchQuality = score,
                Bucket = SearchResultBucket.SystemCommand,
                ResolvedTarget = command.Id,
                SearchInternetText = command.Title,
                CopyText = command.Id
            };
        }

        foreach (var website in WebsiteSuggestions)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromResult<IReadOnlyList<SearchResultItem>>(Array.Empty<SearchResultItem>());
            }

            if (!TryGetAutocompleteScore(website.Name, request, out var score))
            {
                continue;
            }

            if (score <= bestScore)
            {
                continue;
            }

            bestScore = score;
            bestResult = new SearchResultItem
            {
                Id = $"site::{website.Url}",
                Title = website.Name,
                Subtitle = website.Url,
                Target = website.Url,
                Kind = SearchResultKind.Web,
                ActionKind = ResultActionKind.OpenUri,
                ActionValue = website.Url,
                AutocompleteText = website.Name,
                MatchQuality = score,
                Bucket = SearchResultBucket.Web,
                ResolvedTarget = website.Url,
                SearchInternetText = website.Name,
                CopyText = website.Url
            };
        }

        return bestResult is null
            ? ValueTask.FromResult<IReadOnlyList<SearchResultItem>>(Array.Empty<SearchResultItem>())
            : ValueTask.FromResult<IReadOnlyList<SearchResultItem>>(new[] { bestResult });
    }

    private static bool TryGetAutocompleteScore(string autocompleteText, SearchRequest request, out double score)
    {
        score = 0;

        if (string.IsNullOrWhiteSpace(autocompleteText))
        {
            return false;
        }

        var normalizedAutocompleteText = SearchTextUtility.Normalize(autocompleteText);
        if (string.IsNullOrWhiteSpace(normalizedAutocompleteText))
        {
            return false;
        }

        if (!normalizedAutocompleteText.StartsWith(request.NormalizedQuery, StringComparison.Ordinal))
        {
            return false;
        }

        if (normalizedAutocompleteText.Equals(request.NormalizedQuery, StringComparison.Ordinal))
        {
            return false;
        }

        var suffixLength = normalizedAutocompleteText.Length - request.NormalizedQuery.Length;
        var suffixPenalty = Math.Min(0.22, Math.Max(0, suffixLength) * 0.0065);
        score = 0.985 - suffixPenalty;

        if (autocompleteText.StartsWith(request.Query, StringComparison.OrdinalIgnoreCase))
        {
            score += 0.005;
        }

        score = Math.Clamp(score, 0.76, 0.995);
        return true;
    }

    private sealed record CommandSuggestion(string Id, string Title, string Subtitle, ResultActionKind ActionKind);

    private sealed record WebsiteSuggestion(string Name, string Url);
}
