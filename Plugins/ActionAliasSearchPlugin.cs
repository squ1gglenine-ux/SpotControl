using SpotCont.Models;
using SpotCont.Services;

namespace SpotCont.Plugins;

public sealed class ActionAliasSearchPlugin : SearchPluginBase
{
    private readonly ActionAliasService _actionAliasService;

    public ActionAliasSearchPlugin(ActionAliasService actionAliasService)
        : base("action-aliases", "Action Aliases", 15, "Provides fast alias actions such as gh, yt and maps.")
    {
        _actionAliasService = actionAliasService;
    }

    public override async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        await _actionAliasService.InitializeAsync(cancellationToken);
    }

    public override ValueTask<IReadOnlyList<SearchResultItem>> SearchAsync(
        SearchRequest request,
        int maxResults,
        CancellationToken cancellationToken)
    {
        if (request.IsEmpty || maxResults <= 0)
        {
            return ValueTask.FromResult<IReadOnlyList<SearchResultItem>>(Array.Empty<SearchResultItem>());
        }

        var aliases = _actionAliasService.GetAliases();
        if (aliases.Count == 0)
        {
            return ValueTask.FromResult<IReadOnlyList<SearchResultItem>>(Array.Empty<SearchResultItem>());
        }

        var rawQuery = request.Query.Trim();
        var (aliasToken, arguments) = SplitQuery(rawQuery);
        var normalizedAliasToken = SearchTextUtility.Normalize(aliasToken).Replace(" ", string.Empty, StringComparison.Ordinal);
        var noWhitespaceQuery = !rawQuery.Contains(' ');

        var results = new List<SearchResultItem>();

        foreach (var alias in aliases)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromResult<IReadOnlyList<SearchResultItem>>(Array.Empty<SearchResultItem>());
            }

            if (!string.IsNullOrWhiteSpace(normalizedAliasToken) &&
                string.Equals(normalizedAliasToken, alias.NormalizedAlias, StringComparison.Ordinal))
            {
                results.Add(CreateAliasExecutionResult(alias, arguments));
                continue;
            }

            if (noWhitespaceQuery &&
                alias.NormalizedAlias.StartsWith(request.NormalizedQuery, StringComparison.Ordinal))
            {
                var suffixPenalty = Math.Min(0.09, Math.Max(0, alias.NormalizedAlias.Length - request.NormalizedQuery.Length) * 0.013);
                results.Add(CreateAliasSuggestionResult(alias, 0.94 - suffixPenalty));
                continue;
            }

            if (noWhitespaceQuery &&
                SearchTextUtility.TryGetMatchQuality(alias.Alias, alias.SearchText, request, out var matchQuality) &&
                matchQuality >= 0.68)
            {
                results.Add(CreateAliasSuggestionResult(alias, Math.Min(0.91, matchQuality)));
            }
        }

        return ValueTask.FromResult<IReadOnlyList<SearchResultItem>>(results
            .GroupBy(result => result.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.MatchQuality).First())
            .OrderByDescending(item => item.MatchQuality)
            .Take(maxResults)
            .ToArray());
    }

    private static SearchResultItem CreateAliasExecutionResult(ActionAliasDefinition alias, string arguments)
    {
        var hasArguments = !string.IsNullOrWhiteSpace(arguments);
        var target = BuildTarget(alias, arguments);
        var normalizedArguments = SearchTextUtility.Normalize(arguments);
        var idSuffix = string.IsNullOrWhiteSpace(normalizedArguments) ? "default" : normalizedArguments;

        return new SearchResultItem
        {
            Id = $"alias::{alias.Id}::{idSuffix}",
            Title = hasArguments ? $"{alias.Title}: {arguments}" : alias.Title,
            Subtitle = alias.Subtitle,
            Target = target,
            Kind = SearchResultKind.Command,
            ActionKind = ResultActionKind.OpenUri,
            ActionValue = target,
            AutocompleteText = alias.RequiresArgument ? $"{alias.Alias} " : alias.Alias,
            MatchQuality = hasArguments ? 1.0 : 0.985,
            Bucket = SearchResultBucket.Alias,
            ResolvedTarget = target,
            SearchInternetText = hasArguments ? arguments : alias.Title,
            CopyText = target
        };
    }

    private static SearchResultItem CreateAliasSuggestionResult(ActionAliasDefinition alias, double matchQuality)
    {
        var target = BuildTarget(alias, string.Empty);

        return new SearchResultItem
        {
            Id = $"alias::{alias.Id}",
            Title = $"{alias.Alias} - {alias.Title}",
            Subtitle = alias.Subtitle,
            Target = target,
            Kind = SearchResultKind.Command,
            ActionKind = ResultActionKind.OpenUri,
            ActionValue = target,
            AutocompleteText = alias.RequiresArgument ? $"{alias.Alias} " : alias.Alias,
            MatchQuality = matchQuality,
            Bucket = SearchResultBucket.Alias,
            ResolvedTarget = target,
            SearchInternetText = alias.Title,
            CopyText = target
        };
    }

    private static (string Alias, string Arguments) SplitQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return (string.Empty, string.Empty);
        }

        var firstSpaceIndex = query.IndexOf(' ');
        if (firstSpaceIndex < 0)
        {
            return (query, string.Empty);
        }

        var aliasToken = query[..firstSpaceIndex];
        var arguments = query[(firstSpaceIndex + 1)..].Trim();
        return (aliasToken, arguments);
    }

    private static string BuildTarget(ActionAliasDefinition alias, string arguments)
    {
        var rawArguments = (arguments ?? string.Empty).Trim();
        var escapedArguments = Uri.EscapeDataString(rawArguments);
        var target = alias.UrlTemplate
            .Replace("{query}", escapedArguments, StringComparison.OrdinalIgnoreCase)
            .Replace("{raw}", rawArguments, StringComparison.OrdinalIgnoreCase);

        return target.Trim();
    }
}
