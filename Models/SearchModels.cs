using System.IO;
using System.Windows.Media;
using SpotCont.Infrastructure;

namespace SpotCont.Models;

public enum SearchResultKind
{
    Application,
    File,
    Folder,
    Web,
    Command,
    Calculator,
    SystemCommand
}

public enum ResultActionKind
{
    OpenShellTarget,
    OpenUri,
    SearchWeb,
    CopyText,
    Shutdown,
    Restart,
    Lock
}

public enum SearchResultBucket
{
    Calculator,
    SystemCommand,
    Alias,
    Shortcut,
    Executable,
    Folder,
    File,
    Web
}

public enum SearchResultContextActionKind
{
    Open,
    RunAsAdministrator,
    OpenContainingFolder,
    SearchOnInternet,
    Copy
}

public sealed record SearchRequest(string Query, string NormalizedQuery, IReadOnlyList<string> Tokens, bool IsEmpty);

public sealed record IndexedItem(
    string Id,
    string Title,
    string Subtitle,
    string Target,
    SearchResultKind Kind,
    string SearchText,
    string AutocompleteText,
    DateTimeOffset? LastModifiedUtc = null,
    SearchResultBucket Bucket = SearchResultBucket.File,
    string? ResolvedTarget = null,
    string? ContainingDirectoryPath = null,
    bool UsesThumbnailPreview = false,
    string? SearchInternetText = null,
    string? CopyText = null);

public sealed record SearchResultContextAction(SearchResultContextActionKind Kind, string Title);

public sealed class SearchResultItem : ObservableObject
{
    private static readonly HashSet<string> ElevatedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe",
        ".bat",
        ".cmd",
        ".msi",
        ".ps1"
    };

    private ImageSource? _iconSource;
    private string _shortcutLabel = string.Empty;
    private bool _isSelected;

    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string Subtitle { get; init; }

    public required string Target { get; init; }

    public required SearchResultKind Kind { get; init; }

    public required ResultActionKind ActionKind { get; init; }

    public required string ActionValue { get; init; }

    public required string AutocompleteText { get; init; }

    public SearchResultBucket Bucket { get; init; }

    public string ResolvedTarget { get; init; } = string.Empty;

    public string ContainingDirectoryPath { get; init; } = string.Empty;

    public string SearchInternetText { get; init; } = string.Empty;

    public string CopyText { get; init; } = string.Empty;

    public bool UsesThumbnailPreview { get; init; }

    public double MatchQuality { get; init; }

    public double FrequencyScore { get; set; }

    public double RecencyScore { get; set; }

    public double LearningScore { get; set; }

    public double Score { get; set; }

    public int LaunchCount { get; set; }

    public DateTimeOffset? LastLaunchedUtc { get; set; }

    public bool UsesShellIcon =>
        ActionKind == ResultActionKind.OpenShellTarget &&
        (Kind == SearchResultKind.Application || Kind == SearchResultKind.File || Kind == SearchResultKind.Folder);

    public bool SupportsRunAsAdministrator =>
        ActionKind == ResultActionKind.OpenShellTarget &&
        !string.IsNullOrWhiteSpace(ResolvedTarget) &&
        (Kind == SearchResultKind.Application ||
         ElevatedExtensions.Contains(Path.GetExtension(ResolvedTarget)));

    public bool SupportsOpenContainingFolder =>
        !string.IsNullOrWhiteSpace(ContainingDirectoryPath) || Kind == SearchResultKind.Folder;

    public ImageSource? IconSource
    {
        get => _iconSource;
        set => SetProperty(ref _iconSource, value);
    }

    public string ShortcutLabel
    {
        get => _shortcutLabel;
        set => SetProperty(ref _shortcutLabel, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

public static class SearchResultFactory
{
    public static SearchResultItem FromIndexedItem(
        IndexedItem item,
        double matchQuality,
        ResultActionKind actionKind = ResultActionKind.OpenShellTarget,
        string? actionValue = null)
    {
        return new SearchResultItem
        {
            Id = item.Id,
            Title = item.Title,
            Subtitle = item.Subtitle,
            Target = item.Target,
            Kind = item.Kind,
            ActionKind = actionKind,
            ActionValue = actionValue ?? item.Target,
            AutocompleteText = item.AutocompleteText,
            MatchQuality = matchQuality,
            Bucket = item.Bucket,
            ResolvedTarget = item.ResolvedTarget ?? item.Target,
            ContainingDirectoryPath = item.ContainingDirectoryPath ?? GetContainingDirectory(item),
            UsesThumbnailPreview = item.UsesThumbnailPreview,
            SearchInternetText = item.SearchInternetText ?? item.Title,
            CopyText = item.CopyText ?? (actionValue ?? item.Target)
        };
    }

    private static string GetContainingDirectory(IndexedItem item)
    {
        try
        {
            if (item.Kind == SearchResultKind.Folder)
            {
                return Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(item.Target)) ?? item.Target;
            }

            return Path.GetDirectoryName(item.Target) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
