# SpotCont Extensions

Drop compiled plugin assemblies into this folder.

A plugin can work in one of two ways:

1. Implement `SpotCont.Plugins.ISearchPlugin` with a public parameterless constructor.
2. Implement `SpotCont.Plugins.ISearchPluginFactory` and create plugins through `CreatePlugins(SearchPluginContext context)`.

Helpful building blocks:

- `SearchPluginBase` gives you default metadata and initialization behavior.
- `SearchPluginContext` exposes indexed applications, indexed files and usage history.
- `SearchResultFactory.FromIndexedItem(...)` creates consistent result objects with the right metadata for ranking, icons and context actions.

Minimal example:

```csharp
using SpotCont.Models;
using SpotCont.Plugins;

public sealed class SamplePlugin : SearchPluginBase
{
    public SamplePlugin() : base("sample", "Sample Plugin", 60, "Example external plugin.")
    {
    }

    public override ValueTask<IReadOnlyList<SearchResultItem>> SearchAsync(
        SearchRequest request,
        int maxResults,
        CancellationToken cancellationToken)
    {
        if (request.IsEmpty || !request.NormalizedQuery.Contains("sample", StringComparison.Ordinal))
        {
            return ValueTask.FromResult<IReadOnlyList<SearchResultItem>>(Array.Empty<SearchResultItem>());
        }

        return ValueTask.FromResult<IReadOnlyList<SearchResultItem>>(new[]
        {
            new SearchResultItem
            {
                Id = "sample::hello",
                Title = "Sample result",
                Subtitle = "Loaded from an external extension.",
                Target = "https://example.com",
                Kind = SearchResultKind.Web,
                ActionKind = ResultActionKind.OpenUri,
                ActionValue = "https://example.com",
                AutocompleteText = "Sample result",
                MatchQuality = 0.95,
                Bucket = SearchResultBucket.Web,
                ResolvedTarget = "https://example.com",
                SearchInternetText = "Sample result",
                CopyText = "https://example.com"
            }
        });
    }
}
```
