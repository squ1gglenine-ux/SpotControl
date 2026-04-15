using SpotCont.Models;
using SpotCont.Services;

namespace SpotCont.Plugins;

public sealed class CommandSearchPlugin : SearchPluginBase
{
    private static readonly CommandDefinition[] Commands =
    {
        new(
            "shutdown",
            "Shut down Windows",
            "Turn off the device immediately.",
            ResultActionKind.Shutdown,
            "shutdown power off turn off shut down выключить выключение завершить работу"),
        new(
            "restart",
            "Restart Windows",
            "Restart the device immediately.",
            ResultActionKind.Restart,
            "restart reboot перезагрузка перезапустить"),
        new(
            "lock",
            "Lock Windows session",
            "Lock the current account instantly.",
            ResultActionKind.Lock,
            "lock lock screen lock windows lock pc блокировка заблокировать")
    };

    public CommandSearchPlugin()
        : base("commands", "System Commands", 20, "Provides calculator results and built-in Windows commands.")
    {
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
        if (request.IsEmpty)
        {
            return ValueTask.FromResult<IReadOnlyList<SearchResultItem>>(Array.Empty<SearchResultItem>());
        }

        var results = new List<SearchResultItem>();

        if (MathExpressionEvaluator.TryEvaluateQuery(request.Query, out var calculatedResult))
        {
            results.Add(new SearchResultItem
            {
                Id = $"calc::{request.Query}",
                Title = $"= {calculatedResult}",
                Subtitle = "Press Enter to copy the result.",
                Target = request.Query,
                Kind = SearchResultKind.Calculator,
                ActionKind = ResultActionKind.CopyText,
                ActionValue = calculatedResult,
                AutocompleteText = request.Query,
                MatchQuality = 1.0,
                Bucket = SearchResultBucket.Calculator,
                ResolvedTarget = calculatedResult,
                SearchInternetText = request.Query,
                CopyText = calculatedResult
            });
        }

        foreach (var command in Commands)
        {
            if (!SearchTextUtility.TryGetMatchQuality(command.Title, command.SearchText, request, out var matchQuality) ||
                matchQuality < 0.46)
            {
                continue;
            }

            results.Add(new SearchResultItem
            {
                Id = $"command::{command.Id}",
                Title = command.Title,
                Subtitle = command.Subtitle,
                Target = command.Id,
                Kind = SearchResultKind.SystemCommand,
                ActionKind = command.ActionKind,
                ActionValue = command.Id,
                AutocompleteText = command.Id,
                MatchQuality = matchQuality,
                Bucket = SearchResultBucket.SystemCommand,
                ResolvedTarget = command.Id,
                SearchInternetText = command.Title,
                CopyText = command.Id
            });
        }

        return ValueTask.FromResult<IReadOnlyList<SearchResultItem>>(results
            .OrderByDescending(item => item.MatchQuality)
            .Take(maxResults)
            .ToArray());
    }

    private sealed record CommandDefinition(
        string Id,
        string Title,
        string Subtitle,
        ResultActionKind ActionKind,
        string SearchText);
}
