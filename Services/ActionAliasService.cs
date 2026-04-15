using System.IO;
using System.Text.Json;

namespace SpotCont.Services;

public sealed class ActionAliasService
{
    private static readonly JsonSerializerOptions SaveOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _syncRoot = new();
    private readonly string _aliasesFilePath;
    private ActionAliasDefinition[] _aliases;

    public ActionAliasService()
    {
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SpotCont");

        _aliasesFilePath = Path.Combine(appDataPath, "action-aliases.json");
        _aliases = BuildDefaultAliases().ToArray();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ActionAliasDefinition[] aliasesToUse;

        try
        {
            if (!File.Exists(_aliasesFilePath))
            {
                aliasesToUse = BuildDefaultAliases().ToArray();
                await PersistAliasesAsync(aliasesToUse, cancellationToken);
            }
            else
            {
                await using var stream = File.OpenRead(_aliasesFilePath);
                var snapshot = await JsonSerializer.DeserializeAsync<ActionAliasSnapshot>(stream, cancellationToken: cancellationToken);
                aliasesToUse = SanitizeAliases(snapshot?.Aliases).ToArray();

                if (aliasesToUse.Length == 0)
                {
                    aliasesToUse = BuildDefaultAliases().ToArray();
                }

                await PersistAliasesAsync(aliasesToUse, cancellationToken);
            }
        }
        catch
        {
            aliasesToUse = BuildDefaultAliases().ToArray();
        }

        lock (_syncRoot)
        {
            _aliases = aliasesToUse;
        }
    }

    public IReadOnlyList<ActionAliasDefinition> GetAliases()
    {
        lock (_syncRoot)
        {
            return _aliases;
        }
    }

    private async Task PersistAliasesAsync(
        IReadOnlyList<ActionAliasDefinition> aliases,
        CancellationToken cancellationToken)
    {
        var directoryPath = Path.GetDirectoryName(_aliasesFilePath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        var snapshot = new ActionAliasSnapshot
        {
            Aliases = aliases
                .Select(alias => new ActionAliasEntry
                {
                    Id = alias.Id,
                    Alias = alias.Alias,
                    Title = alias.Title,
                    Subtitle = alias.Subtitle,
                    UrlTemplate = alias.UrlTemplate,
                    RequiresArgument = alias.RequiresArgument,
                    SearchText = alias.SearchText
                })
                .ToList()
        };

        var temporaryPath = $"{_aliasesFilePath}.tmp";

        await using (var stream = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.Read))
        {
            await JsonSerializer.SerializeAsync(stream, snapshot, SaveOptions, cancellationToken);
        }

        File.Move(temporaryPath, _aliasesFilePath, true);
    }

    private static IReadOnlyList<ActionAliasDefinition> SanitizeAliases(IReadOnlyList<ActionAliasEntry>? entries)
    {
        if (entries is null || entries.Count == 0)
        {
            return BuildDefaultAliases();
        }

        var aliases = new List<ActionAliasDefinition>();
        var usedAliases = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (!TryCreateAliasDefinition(entry, out var alias))
            {
                continue;
            }

            if (!usedAliases.Add(alias.NormalizedAlias))
            {
                continue;
            }

            aliases.Add(alias);
        }

        return aliases;
    }

    private static bool TryCreateAliasDefinition(ActionAliasEntry? entry, out ActionAliasDefinition alias)
    {
        alias = null!;
        if (entry is null)
        {
            return false;
        }

        var aliasToken = (entry.Alias ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(aliasToken))
        {
            return false;
        }

        var normalizedAlias = SearchTextUtility.Normalize(aliasToken).Replace(" ", string.Empty, StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(normalizedAlias) || normalizedAlias.Length > 32)
        {
            return false;
        }

        var urlTemplate = (entry.UrlTemplate ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(urlTemplate))
        {
            return false;
        }

        if (!Uri.TryCreate(urlTemplate, UriKind.Absolute, out _) &&
            !urlTemplate.Contains("{query}", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var title = string.IsNullOrWhiteSpace(entry.Title)
            ? normalizedAlias
            : entry.Title.Trim();
        var subtitle = string.IsNullOrWhiteSpace(entry.Subtitle)
            ? $"Alias: {normalizedAlias}"
            : entry.Subtitle.Trim();
        var searchText = string.IsNullOrWhiteSpace(entry.SearchText)
            ? $"{normalizedAlias} {title} {subtitle}"
            : $"{normalizedAlias} {title} {subtitle} {entry.SearchText.Trim()}";
        var requiresArgument = entry.RequiresArgument ||
                               urlTemplate.Contains("{query}", StringComparison.OrdinalIgnoreCase);

        alias = new ActionAliasDefinition(
            string.IsNullOrWhiteSpace(entry.Id) ? normalizedAlias : entry.Id.Trim(),
            normalizedAlias,
            title,
            subtitle,
            urlTemplate,
            requiresArgument,
            searchText);

        return true;
    }

    private static IReadOnlyList<ActionAliasDefinition> BuildDefaultAliases()
    {
        return new[]
        {
            new ActionAliasDefinition(
                "google-search",
                "g",
                "Google Search",
                "g <query> - search in Google",
                "https://www.google.com/search?q={query}",
                true,
                "google search"),
            new ActionAliasDefinition(
                "youtube-search",
                "yt",
                "YouTube Search",
                "yt <query> - search on YouTube",
                "https://www.youtube.com/results?search_query={query}",
                true,
                "youtube yt video"),
            new ActionAliasDefinition(
                "github-search",
                "gh",
                "GitHub Search",
                "gh <query> - search repositories and code",
                "https://github.com/search?q={query}",
                true,
                "github code repository"),
            new ActionAliasDefinition(
                "wikipedia-search",
                "w",
                "Wikipedia Search",
                "w <query> - search in Wikipedia",
                "https://www.wikipedia.org/w/index.php?search={query}",
                true,
                "wikipedia wiki"),
            new ActionAliasDefinition(
                "maps-search",
                "maps",
                "Maps Search",
                "maps <query> - open location search",
                "https://www.google.com/maps/search/{query}",
                true,
                "maps location"),
            new ActionAliasDefinition(
                "open-mail",
                "mail",
                "Open Mail",
                "Open Gmail inbox",
                "https://mail.google.com",
                false,
                "mail gmail inbox")
        };
    }

    private sealed class ActionAliasSnapshot
    {
        public List<ActionAliasEntry> Aliases { get; set; } = new();
    }

    private sealed class ActionAliasEntry
    {
        public string Id { get; set; } = string.Empty;

        public string Alias { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string Subtitle { get; set; } = string.Empty;

        public string UrlTemplate { get; set; } = string.Empty;

        public bool RequiresArgument { get; set; }

        public string SearchText { get; set; } = string.Empty;
    }
}

public sealed record ActionAliasDefinition(
    string Id,
    string Alias,
    string Title,
    string Subtitle,
    string UrlTemplate,
    bool RequiresArgument,
    string SearchText)
{
    public string NormalizedAlias => SearchTextUtility.Normalize(Alias);
}
