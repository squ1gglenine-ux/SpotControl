using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using SpotCont.Infrastructure;
using SpotCont.Plugins;
using SpotCont.Services;
using SpotCont.ViewModels;

namespace SpotCont;

public partial class App : Application
{
    private static readonly object LogSyncRoot = new();
    private MainWindow? _mainWindow;
    private HotkeyManager? _hotkeyManager;
    private ThemeService? _themeService;

    protected override async void OnStartup(StartupEventArgs e)
    {
        try
        {
            base.OnStartup(e);

            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            try
            {
                _themeService = new ThemeService(this);
                _themeService.Start();
            }
            catch (Exception exception)
            {
                ReportException("Theme", exception);
                _themeService?.Dispose();
                _themeService = null;
            }

            var usageHistoryService = new UsageHistoryService();
            var actionAliasService = new ActionAliasService();
            var iconCacheService = new IconCacheService();
            var applicationIndexService = new ApplicationIndexService();
            var fileIndexService = new FileIndexService();
            var launcherService = new ResultLauncherService(usageHistoryService);
            var extensionsDirectory = Path.Combine(AppContext.BaseDirectory, "Extensions");
            var pluginContext = new SearchPluginContext(
                applicationIndexService,
                fileIndexService,
                usageHistoryService,
                extensionsDirectory);
            var builtInPlugins = new ISearchPlugin[]
            {
                new AutocompleteSearchPlugin(applicationIndexService, fileIndexService),
                new ActionAliasSearchPlugin(actionAliasService),
                new CommandSearchPlugin(),
                new ApplicationSearchPlugin(applicationIndexService),
                new FileSearchPlugin(fileIndexService),
                new WebSearchPlugin()
            };
            var externalPluginLoader = new ExternalSearchPluginLoader(extensionsDirectory);
            var externalPlugins = externalPluginLoader.LoadPlugins(pluginContext);
            var pluginHost = new SearchPluginHost(builtInPlugins.Concat(externalPlugins));
            var searchEngine = new SearchEngine(pluginHost, usageHistoryService);
            var viewModel = new LauncherViewModel(
                searchEngine,
                launcherService,
                iconCacheService,
                applicationIndexService,
                fileIndexService,
                usageHistoryService,
                _themeService);

            await usageHistoryService.InitializeAsync();

            _mainWindow = new MainWindow(viewModel);
            _mainWindow.Closed += MainWindow_Closed;
            MainWindow = _mainWindow;

            _hotkeyManager = new HotkeyManager(HotkeyModifiers.Alt, System.Windows.Input.Key.Space);
            _hotkeyManager.HotkeyPressed += HotkeyManager_HotkeyPressed;

            viewModel.StartBackgroundInitialization();
        }
        catch (Exception exception)
        {
            ReportException("Startup", exception);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mainWindow?.Dispose();

        if (_hotkeyManager is not null)
        {
            _hotkeyManager.HotkeyPressed -= HotkeyManager_HotkeyPressed;
            _hotkeyManager.Dispose();
            _hotkeyManager = null;
        }

        _themeService?.Dispose();
        _themeService = null;

        base.OnExit(e);
    }

    private async void HotkeyManager_HotkeyPressed(object? sender, EventArgs e)
    {
        if (_mainWindow is null)
        {
            return;
        }

        try
        {
            await _mainWindow.ToggleAsync();
        }
        catch (Exception exception)
        {
            ReportException("Hotkey", exception);
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        Shutdown();
    }

    private static void ReportException(string source, Exception exception)
    {
        var message = $"[SpotCont:{source}] {exception}";
        Debug.WriteLine(message);
        TryWriteLog(message);
    }

    private static void TryWriteLog(string message)
    {
        try
        {
            var appDataDirectoryPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SpotCont");
            Directory.CreateDirectory(appDataDirectoryPath);

            var logFilePath = Path.Combine(appDataDirectoryPath, "runtime.log");
            var logEntry = $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}";

            lock (LogSyncRoot)
            {
                File.AppendAllText(logFilePath, logEntry);
            }
        }
        catch
        {
        }
    }
}
