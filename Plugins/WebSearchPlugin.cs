using SpotCont.Models;
using SpotCont.Services;

namespace SpotCont.Plugins;

public sealed class WebSearchPlugin : SearchPluginBase
{
    private static readonly KnownWebsite[] KnownWebsites =
    {
        new("GitHub", "https://github.com", "github gh git hub"),
        new("Google", "https://www.google.com", "google"),
        new("YouTube", "https://www.youtube.com", "youtube yt"),
        new("Stack Overflow", "https://stackoverflow.com", "stack overflow stackoverflow so"),
        new("ChatGPT", "https://chatgpt.com", "chatgpt gpt openai"),
        new("OpenAI", "https://openai.com", "openai open ai"),
        new("Wikipedia", "https://www.wikipedia.org", "wikipedia wiki")
    };

    public WebSearchPlugin()
        : base("web", "Web", 50, "Recognizes URLs, known websites and web searches.")
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
        var query = request.Query.Trim();

        if (TryNormalizeUrl(query, out var directUri))
        {
            results.Add(new SearchResultItem
            {
                Id = $"url::{directUri.AbsoluteUri}",
                Title = directUri.Host,
                Subtitle = $"Open {directUri.AbsoluteUri}",
                Target = directUri.AbsoluteUri,
                Kind = SearchResultKind.Web,
                ActionKind = ResultActionKind.OpenUri,
                ActionValue = directUri.AbsoluteUri,
                AutocompleteText = directUri.Host,
                MatchQuality = 0.88,
                Bucket = SearchResultBucket.Web,
                ResolvedTarget = directUri.AbsoluteUri,
                SearchInternetText = directUri.Host,
                CopyText = directUri.AbsoluteUri
            });
        }
        else if (!query.Contains(' '))
        {
            foreach (var website in KnownWebsites)
            {
                if (!SearchTextUtility.TryGetMatchQuality(
                        website.Name,
                        $"{website.Url} {website.SearchText}",
                        request,
                        out var matchQuality) ||
                    matchQuality < 0.5)
                {
                    continue;
                }

                results.Add(new SearchResultItem
                {
                    Id = $"site::{website.Url}",
                    Title = website.Name,
                    Subtitle = website.Url,
                    Target = website.Url,
                    Kind = SearchResultKind.Web,
                    ActionKind = ResultActionKind.OpenUri,
                    ActionValue = website.Url,
                    AutocompleteText = website.Name,
                    MatchQuality = matchQuality,
                    Bucket = SearchResultBucket.Web,
                    ResolvedTarget = website.Url,
                    SearchInternetText = website.Name,
                    CopyText = website.Url
                });
            }
        }

        results.Add(new SearchResultItem
        {
            Id = $"websearch::{query}",
            Title = $"Search the web for \"{query}\"",
            Subtitle = "Open a browser search.",
            Target = query,
            Kind = SearchResultKind.Web,
            ActionKind = ResultActionKind.SearchWeb,
            ActionValue = query,
            AutocompleteText = query,
            MatchQuality = query.Contains(' ') ? 0.4 : 0.28,
            Bucket = SearchResultBucket.Web,
            ResolvedTarget = query,
            SearchInternetText = query,
            CopyText = query
        });

        return ValueTask.FromResult<IReadOnlyList<SearchResultItem>>(results
            .GroupBy(item => item.Id)
            .Select(group => group.OrderByDescending(item => item.MatchQuality).First())
            .OrderByDescending(item => item.MatchQuality)
            .Take(maxResults)
            .ToArray());
    }

    private static bool TryNormalizeUrl(string query, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(query) || query.Contains(' '))
        {
            return false;
        }

        if (Uri.TryCreate(query, UriKind.Absolute, out var absoluteUri) &&
            (absoluteUri.Scheme == Uri.UriSchemeHttp || absoluteUri.Scheme == Uri.UriSchemeHttps))
        {
            uri = absoluteUri;
            return true;
        }

        if (!query.Contains('.'))
        {
            return false;
        }

        if (Uri.TryCreate($"https://{query}", UriKind.Absolute, out var implicitUri))
        {
            uri = implicitUri;
            return true;
        }

        return false;
    }

    private sealed record KnownWebsite(string Name, string Url, string SearchText);
}
