using SpotCont.Models;
using SpotCont.Services;

namespace SpotCont.Plugins;

public sealed record SearchPluginMetadata(
    string Id,
    string DisplayName,
    int Order = 0,
    string Description = "");

public sealed class SearchPluginContext
{
    public SearchPluginContext(
        ApplicationIndexService applicationIndexService,
        FileIndexService fileIndexService,
        UsageHistoryService usageHistoryService,
        string extensionsDirectory)
    {
        ApplicationIndexService = applicationIndexService;
        FileIndexService = fileIndexService;
        UsageHistoryService = usageHistoryService;
        ExtensionsDirectory = extensionsDirectory;
    }

    public ApplicationIndexService ApplicationIndexService { get; }

    public FileIndexService FileIndexService { get; }

    public UsageHistoryService UsageHistoryService { get; }

    public string ExtensionsDirectory { get; }
}

public interface ISearchPluginFactory
{
    IEnumerable<ISearchPlugin> CreatePlugins(SearchPluginContext context);
}

public interface ISearchPlugin
{
    SearchPluginMetadata Metadata { get; }

    ValueTask InitializeAsync(CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<SearchResultItem>> SearchAsync(
        SearchRequest request,
        int maxResults,
        CancellationToken cancellationToken);
}

public abstract class SearchPluginBase : ISearchPlugin
{
    protected SearchPluginBase(string id, string displayName, int order = 0, string description = "")
    {
        Metadata = new SearchPluginMetadata(id, displayName, order, description);
    }

    public SearchPluginMetadata Metadata { get; }

    public virtual ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }

    public abstract ValueTask<IReadOnlyList<SearchResultItem>> SearchAsync(
        SearchRequest request,
        int maxResults,
        CancellationToken cancellationToken);

    protected static SearchResultItem CreateResult(
        IndexedItem item,
        double matchQuality,
        ResultActionKind actionKind = ResultActionKind.OpenShellTarget,
        string? actionValue = null)
    {
        return SearchResultFactory.FromIndexedItem(item, matchQuality, actionKind, actionValue);
    }
}
