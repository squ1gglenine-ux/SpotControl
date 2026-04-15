using SpotCont.Plugins;

namespace SpotCont.Services;

public sealed class SearchPluginHost
{
    public SearchPluginHost(IEnumerable<ISearchPlugin> plugins)
    {
        Plugins = plugins
            .OrderBy(plugin => plugin.Metadata.Order)
            .ThenBy(plugin => plugin.Metadata.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<ISearchPlugin> Plugins { get; }
}
