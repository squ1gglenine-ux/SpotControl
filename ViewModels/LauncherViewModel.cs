using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using SpotCont.Infrastructure;
using SpotCont.Models;
using SpotCont.Services;

namespace SpotCont.ViewModels;

public sealed class LauncherViewModel : ObservableObject, IDisposable
{
    private const int MaxVisibleResults = 15;
    private const double CompactLauncherHeight = 56;
    private const double ExpandedLauncherHeight = 384;
    private const double LargeFilePreviewImageSize = 176;
    private const double CompactFilePreviewImageSize = 92;
    private const double CompactFilePreviewIconSize = 72;
    private const int PreviewSnippetMaxLength = 2600;
    private const int PreviewTextReadChars = 8192;
    private const int FolderPreviewMaxItems = 16;
    private static readonly HashSet<string> LargePreviewImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".bmp",
        ".gif",
        ".webp",
        ".tif",
        ".tiff"
    };

    private readonly SearchEngine _searchEngine;
    private readonly ResultLauncherService _resultLauncherService;
    private readonly IconCacheService _iconCacheService;
    private readonly ApplicationIndexService _applicationIndexService;
    private readonly FileIndexService _fileIndexService;
    private readonly ThemeService? _themeService;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _searchDebounceTimer;
    private CancellationTokenSource? _searchCancellationTokenSource;
    private CancellationTokenSource? _filePreviewCancellationTokenSource;
    private string _query = string.Empty;
    private string _currentTimeText = DateTime.Now.ToString("hh:mm tt", CultureInfo.InvariantCulture);
    private string _searchIconSource = GetSearchIconSourcePath();
    private string _filePreviewTitle = string.Empty;
    private string _filePreviewPath = string.Empty;
    private string _filePreviewType = string.Empty;
    private string _filePreviewSize = string.Empty;
    private string _filePreviewModified = string.Empty;
    private string _filePreviewSnippet = string.Empty;
    private string _autocompleteSuffix = string.Empty;
    private string _autocompletePreviewText = string.Empty;
    private ImageSource? _filePreviewImageSource;
    private SearchResultItem? _selectedResult;
    private Task? _initializationTask;
    private DateTimeOffset _mouseSelectionBlockedUntilUtc = DateTimeOffset.MinValue;
    private int _searchVersion;
    private int _filePreviewVersion;
    private bool _hasResults;
    private bool _hasFilePreview;
    private bool _usesLargeFilePreviewImage;
    private bool _isLauncherVisible;
    private bool _isDisposed;

    public LauncherViewModel(
        SearchEngine searchEngine,
        ResultLauncherService resultLauncherService,
        IconCacheService iconCacheService,
        ApplicationIndexService applicationIndexService,
        FileIndexService fileIndexService,
        UsageHistoryService usageHistoryService,
        ThemeService? themeService = null)
    {
        _searchEngine = searchEngine;
        _resultLauncherService = resultLauncherService;
        _iconCacheService = iconCacheService;
        _applicationIndexService = applicationIndexService;
        _fileIndexService = fileIndexService;
        _themeService = themeService;
        Results = new ObservableCollection<SearchResultItem>();

        _clockTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(15)
        };
        _clockTimer.Tick += (_, _) => CurrentTimeText = DateTime.Now.ToString("hh:mm tt", CultureInfo.InvariantCulture);
        _searchDebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(70)
        };
        _searchDebounceTimer.Tick += SearchDebounceTimer_Tick;

        _applicationIndexService.IndexUpdated += IndexService_IndexUpdated;
        _fileIndexService.IndexUpdated += IndexService_IndexUpdated;

        if (_themeService is not null)
        {
            _themeService.ThemeChanged += ThemeService_ThemeChanged;
        }

        RefreshSearchIconSource();
    }

    public event EventHandler? HideRequested;

    public ObservableCollection<SearchResultItem> Results { get; }

    public string Query
    {
        get => _query;
        set
        {
            if (!SetProperty(ref _query, value))
            {
                return;
            }

            ClearAutocompletePreview();

            if (string.IsNullOrWhiteSpace(value))
            {
                _searchDebounceTimer.Stop();
                _ = RefreshResultsAsync();
                return;
            }

            _searchDebounceTimer.Stop();
            _searchDebounceTimer.Start();
        }
    }

    public string CurrentTimeText
    {
        get => _currentTimeText;
        private set => SetProperty(ref _currentTimeText, value);
    }

    public string SearchIconSource
    {
        get => _searchIconSource;
        private set => SetProperty(ref _searchIconSource, value);
    }

    public bool HasFilePreview
    {
        get => _hasFilePreview;
        private set => SetProperty(ref _hasFilePreview, value);
    }

    public bool UsesLargeFilePreviewImage
    {
        get => _usesLargeFilePreviewImage;
        private set
        {
            if (!SetProperty(ref _usesLargeFilePreviewImage, value))
            {
                return;
            }

            OnPropertyChanged(nameof(FilePreviewImageContainerHeight));
            OnPropertyChanged(nameof(FilePreviewImageContainerWidth));
            OnPropertyChanged(nameof(FilePreviewImageContainerAlignment));
            OnPropertyChanged(nameof(FilePreviewImageMaxDimension));
            OnPropertyChanged(nameof(FilePreviewImageStretch));
        }
    }

    public double FilePreviewImageContainerHeight =>
        UsesLargeFilePreviewImage ? LargeFilePreviewImageSize : CompactFilePreviewImageSize;

    public double FilePreviewImageContainerWidth =>
        UsesLargeFilePreviewImage ? double.NaN : CompactFilePreviewImageSize;

    public HorizontalAlignment FilePreviewImageContainerAlignment =>
        UsesLargeFilePreviewImage ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;

    public double FilePreviewImageMaxDimension =>
        UsesLargeFilePreviewImage ? double.PositiveInfinity : CompactFilePreviewIconSize;

    public Stretch FilePreviewImageStretch =>
        UsesLargeFilePreviewImage ? Stretch.Uniform : Stretch.None;

    public string FilePreviewTitle
    {
        get => _filePreviewTitle;
        private set => SetProperty(ref _filePreviewTitle, value);
    }

    public string FilePreviewPath
    {
        get => _filePreviewPath;
        private set => SetProperty(ref _filePreviewPath, value);
    }

    public string FilePreviewType
    {
        get => _filePreviewType;
        private set => SetProperty(ref _filePreviewType, value);
    }

    public string FilePreviewSize
    {
        get => _filePreviewSize;
        private set => SetProperty(ref _filePreviewSize, value);
    }

    public string FilePreviewModified
    {
        get => _filePreviewModified;
        private set => SetProperty(ref _filePreviewModified, value);
    }

    public string FilePreviewSnippet
    {
        get => _filePreviewSnippet;
        private set
        {
            if (!SetProperty(ref _filePreviewSnippet, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasFilePreviewSnippet));
        }
    }

    public bool HasFilePreviewSnippet => !string.IsNullOrWhiteSpace(FilePreviewSnippet);

    public ImageSource? FilePreviewImageSource
    {
        get => _filePreviewImageSource;
        private set => SetProperty(ref _filePreviewImageSource, value);
    }

    public SearchResultItem? SelectedResult
    {
        get => _selectedResult;
        set
        {
            if (!SetProperty(ref _selectedResult, value))
            {
                return;
            }

            RefreshFilePreview(value);
        }
    }

    public bool HasResults
    {
        get => _hasResults;
        private set
        {
            if (!SetProperty(ref _hasResults, value))
            {
                return;
            }

            OnPropertyChanged(nameof(LauncherHeight));
        }
    }

    public double LauncherHeight => HasResults ? ExpandedLauncherHeight : CompactLauncherHeight;

    public string AutocompleteSuffix
    {
        get => _autocompleteSuffix;
        private set
        {
            if (!SetProperty(ref _autocompleteSuffix, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasAutocompleteSuffix));
        }
    }

    public bool HasAutocompleteSuffix => !string.IsNullOrWhiteSpace(AutocompleteSuffix);

    public void StartBackgroundInitialization()
    {
        _ = EnsureInitializedAsync();
    }

    public void SetLauncherVisibility(bool isVisible)
    {
        _isLauncherVisible = isVisible;
        CurrentTimeText = DateTime.Now.ToString("hh:mm tt", CultureInfo.InvariantCulture);
        RefreshSearchIconSource();

        if (isVisible)
        {
            if (!_clockTimer.IsEnabled)
            {
                _clockTimer.Start();
            }
        }
        else if (_clockTimer.IsEnabled)
        {
            _clockTimer.Stop();
        }
    }

    public async Task RefreshAsync()
    {
        _searchDebounceTimer.Stop();
        await EnsureInitializedAsync();
        await RefreshResultsAsync();
    }

    public void MoveSelection(int delta)
    {
        if (Results.Count == 0)
        {
            SelectedResult = null;
            return;
        }

        _mouseSelectionBlockedUntilUtc = DateTimeOffset.UtcNow.AddMilliseconds(150);

        if (SelectedResult is null)
        {
            SelectedResult = delta >= 0 ? Results[0] : Results[^1];
            return;
        }

        var index = Results.IndexOf(SelectedResult);
        if (index < 0)
        {
            SelectedResult = Results[0];
            return;
        }

        var nextIndex = (index + delta + Results.Count) % Results.Count;
        SelectedResult = Results[nextIndex];
    }

    public void SelectFromMouse(SearchResultItem item)
    {
        if (DateTimeOffset.UtcNow < _mouseSelectionBlockedUntilUtc)
        {
            return;
        }

        SelectedResult = item;
    }

    public bool ApplyAutocomplete()
    {
        var autocompleteText = _autocompletePreviewText;
        if (string.IsNullOrWhiteSpace(autocompleteText))
        {
            autocompleteText = SelectedResult?.AutocompleteText;
        }

        if (string.IsNullOrWhiteSpace(autocompleteText))
        {
            return false;
        }

        if (!autocompleteText.StartsWith(Query, StringComparison.OrdinalIgnoreCase) ||
            autocompleteText.Length <= Query.Length)
        {
            return false;
        }

        Query = autocompleteText;
        return true;
    }

    public async Task ExecuteSelectedAsync()
    {
        var item = SelectedResult ?? Results.FirstOrDefault();
        if (item is null)
        {
            return;
        }

        if (await _resultLauncherService.ExecuteAsync(item, Query))
        {
            HideRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task ExecuteByShortcutAsync(int shortcutIndex)
    {
        if (shortcutIndex < 0 || shortcutIndex >= Results.Count)
        {
            return;
        }

        SelectedResult = Results[shortcutIndex];
        await ExecuteSelectedAsync();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _isLauncherVisible = false;
        _clockTimer.Stop();
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Tick -= SearchDebounceTimer_Tick;
        _applicationIndexService.IndexUpdated -= IndexService_IndexUpdated;
        _fileIndexService.IndexUpdated -= IndexService_IndexUpdated;
        if (_themeService is not null)
        {
            _themeService.ThemeChanged -= ThemeService_ThemeChanged;
        }

        var cancellationTokenSource = Interlocked.Exchange(ref _searchCancellationTokenSource, null);
        TryCancelAndDispose(cancellationTokenSource);

        var filePreviewCancellationTokenSource = Interlocked.Exchange(ref _filePreviewCancellationTokenSource, null);
        TryCancelAndDispose(filePreviewCancellationTokenSource);
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initializationTask is null)
        {
            _initializationTask = InitializeCoreAsync();
        }

        await _initializationTask;
    }

    private async Task InitializeCoreAsync()
    {
        await _searchEngine.InitializeAsync();
        await RefreshResultsAsync();
    }

    private async void SearchDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _searchDebounceTimer.Stop();

        try
        {
            await RefreshResultsAsync();
        }
        catch (Exception exception)
        {
            ReportException("SearchDebounceTick", exception);
        }
    }

    private async Task RefreshResultsAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        var version = Interlocked.Increment(ref _searchVersion);
        var cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = cancellationTokenSource.Token;
        var previousCancellationTokenSource = Interlocked.Exchange(ref _searchCancellationTokenSource, cancellationTokenSource);

        TryCancelAndDispose(previousCancellationTokenSource);

        try
        {
            if (string.IsNullOrWhiteSpace(Query))
            {
                await Application.Current.Dispatcher.InvokeAsync(
                    () => ApplyResults(Array.Empty<SearchResultItem>()),
                    DispatcherPriority.DataBind,
                    cancellationToken);
                return;
            }

            var results = await _searchEngine.SearchAsync(Query, MaxVisibleResults, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            await Application.Current.Dispatcher.InvokeAsync(
                () => ApplyResults(results),
                DispatcherPriority.DataBind,
                cancellationToken);

            await PrimeIconsAsync(results, version, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            ReportException("RefreshResults", exception);
        }
        finally
        {
            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _searchCancellationTokenSource, null, cancellationTokenSource),
                    cancellationTokenSource))
            {
                TryDispose(cancellationTokenSource);
            }
        }
    }

    private async Task PrimeIconsAsync(
        IReadOnlyList<SearchResultItem> results,
        int version,
        CancellationToken cancellationToken)
    {
        foreach (var item in results.Take(MaxVisibleResults))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var icon = await _iconCacheService.GetIconAsync(item, cancellationToken);
            if (cancellationToken.IsCancellationRequested || version != _searchVersion)
            {
                return;
            }

            await Application.Current.Dispatcher.InvokeAsync(
                () =>
                {
                    item.IconSource = icon;
                },
                DispatcherPriority.Background,
                cancellationToken);
        }
    }

    public IReadOnlyList<SearchResultContextAction> GetContextActions(SearchResultItem item)
    {
        var actions = new List<SearchResultContextAction>
        {
            new(SearchResultContextActionKind.Open, GetOpenActionTitle(item))
        };

        if (item.SupportsRunAsAdministrator)
        {
            actions.Add(new(SearchResultContextActionKind.RunAsAdministrator, "Open as administrator"));
        }

        if (item.SupportsOpenContainingFolder)
        {
            actions.Add(new(
                SearchResultContextActionKind.OpenContainingFolder,
                item.Kind == SearchResultKind.Folder ? "Open parent folder" : "Open file location"));
        }

        if (CanSearchOnInternet(item))
        {
            actions.Add(new(SearchResultContextActionKind.SearchOnInternet, "Find on the internet"));
        }

        if (!string.IsNullOrWhiteSpace(item.CopyText))
        {
            actions.Add(new(SearchResultContextActionKind.Copy, GetCopyActionTitle(item)));
        }

        return actions;
    }

    public async Task ExecuteContextActionAsync(
        SearchResultItem item,
        SearchResultContextActionKind actionKind,
        CancellationToken cancellationToken = default)
    {
        if (!await _resultLauncherService.ExecuteContextActionAsync(item, actionKind, Query, cancellationToken))
        {
            return;
        }

        if (actionKind is SearchResultContextActionKind.Open or
            SearchResultContextActionKind.RunAsAdministrator or
            SearchResultContextActionKind.OpenContainingFolder or
            SearchResultContextActionKind.SearchOnInternet)
        {
            HideRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ApplyResults(IReadOnlyList<SearchResultItem> results)
    {
        Results.Clear();

        for (var index = 0; index < results.Count; index++)
        {
            var result = results[index];
            result.ShortcutLabel = index < 9 ? $"Alt+{index + 1}" : string.Empty;
            result.IconSource = _iconCacheService.GetInitialIcon(result);
            Results.Add(result);
        }

        SelectedResult = Results.FirstOrDefault();
        HasResults = Results.Count > 0;
        RefreshAutocompletePreview(results);
    }

    private void RefreshAutocompletePreview(IReadOnlyList<SearchResultItem> results)
    {
        if (string.IsNullOrWhiteSpace(Query))
        {
            ClearAutocompletePreview();
            return;
        }

        var previewText = results
            .Select(item => item.AutocompleteText?.Trim())
            .FirstOrDefault(text =>
                !string.IsNullOrWhiteSpace(text) &&
                text.Length > Query.Length &&
                text.StartsWith(Query, StringComparison.OrdinalIgnoreCase));

        _autocompletePreviewText = previewText ?? string.Empty;

        if (_autocompletePreviewText.Length > Query.Length &&
            _autocompletePreviewText.StartsWith(Query, StringComparison.OrdinalIgnoreCase))
        {
            AutocompleteSuffix = _autocompletePreviewText[Query.Length..];
            return;
        }

        AutocompleteSuffix = string.Empty;
    }

    private void ClearAutocompletePreview()
    {
        _autocompletePreviewText = string.Empty;
        AutocompleteSuffix = string.Empty;
    }

    private async void IndexService_IndexUpdated(object? sender, EventArgs e)
    {
        if (!_isLauncherVisible)
        {
            return;
        }

        try
        {
            await RefreshResultsAsync();
        }
        catch (Exception exception)
        {
            ReportException("IndexUpdated", exception);
        }
    }

    private async void ThemeService_ThemeChanged(object? sender, EventArgs e)
    {
        RefreshSearchIconSource();
        _iconCacheService.InvalidateThemeDependentCache();

        if (!_isLauncherVisible)
        {
            return;
        }

        try
        {
            await RefreshResultsAsync();
        }
        catch (Exception exception)
        {
            ReportException("ThemeChanged", exception);
        }
    }

    private void RefreshFilePreview(SearchResultItem? selectedItem)
    {
        var version = Interlocked.Increment(ref _filePreviewVersion);
        var cancellationTokenSource = new CancellationTokenSource();
        var previousCancellationTokenSource = Interlocked.Exchange(
            ref _filePreviewCancellationTokenSource,
            cancellationTokenSource);
        TryCancelAndDispose(previousCancellationTokenSource);

        if (!TryCreateFilePreviewSnapshot(selectedItem, out var snapshot))
        {
            ClearFilePreview();
            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _filePreviewCancellationTokenSource, null, cancellationTokenSource),
                    cancellationTokenSource))
            {
                TryDispose(cancellationTokenSource);
            }

            return;
        }

        ApplyFilePreviewSnapshot(snapshot, selectedItem!);
        _ = LoadFilePreviewAsync(selectedItem!, snapshot, version, cancellationTokenSource);
    }

    private async Task LoadFilePreviewAsync(
        SearchResultItem selectedItem,
        FilePreviewSnapshot snapshot,
        int version,
        CancellationTokenSource cancellationTokenSource)
    {
        var cancellationToken = cancellationTokenSource.Token;

        try
        {
            var previewIcon = await _iconCacheService.GetPreviewImageAsync(selectedItem, cancellationToken);
            var previewSnippet = await LoadFilePreviewSnippetAsync(
                snapshot.Path,
                selectedItem.Kind,
                cancellationToken);
            if (cancellationToken.IsCancellationRequested ||
                version != _filePreviewVersion ||
                !ReferenceEquals(selectedItem, SelectedResult))
            {
                return;
            }

            await Application.Current.Dispatcher.InvokeAsync(
                () =>
                {
                    if (version != _filePreviewVersion ||
                        !ReferenceEquals(selectedItem, SelectedResult))
                    {
                        return;
                    }

                    FilePreviewImageSource = previewIcon;
                    FilePreviewSnippet = previewSnippet;
                },
                DispatcherPriority.Background,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
        finally
        {
            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _filePreviewCancellationTokenSource, null, cancellationTokenSource),
                    cancellationTokenSource))
            {
                TryDispose(cancellationTokenSource);
            }
        }
    }

    private void ApplyFilePreviewSnapshot(FilePreviewSnapshot snapshot, SearchResultItem selectedItem)
    {
        HasFilePreview = true;
        UsesLargeFilePreviewImage = ShouldUseLargeFilePreviewImage(selectedItem, snapshot.Path);
        FilePreviewTitle = snapshot.Title;
        FilePreviewPath = snapshot.Path;
        FilePreviewType = snapshot.Type;
        FilePreviewSize = snapshot.Size;
        FilePreviewModified = snapshot.Modified;
        FilePreviewSnippet = string.Empty;
        FilePreviewImageSource = _iconCacheService.GetInitialPreviewImage(selectedItem);
    }

    private void ClearFilePreview()
    {
        HasFilePreview = false;
        UsesLargeFilePreviewImage = false;
        FilePreviewTitle = string.Empty;
        FilePreviewPath = string.Empty;
        FilePreviewType = string.Empty;
        FilePreviewSize = string.Empty;
        FilePreviewModified = string.Empty;
        FilePreviewSnippet = string.Empty;
        FilePreviewImageSource = null;
    }

    private static bool TryCreateFilePreviewSnapshot(
        SearchResultItem? selectedItem,
        out FilePreviewSnapshot snapshot)
    {
        snapshot = default;
        if (selectedItem is null ||
            selectedItem.Kind is not (SearchResultKind.File or SearchResultKind.Folder))
        {
            return false;
        }

        var path = GetPreviewPath(selectedItem);
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (selectedItem.Kind == SearchResultKind.Folder)
        {
            if (!Directory.Exists(path))
            {
                return false;
            }

            var directoryInfo = new DirectoryInfo(path);
            snapshot = new FilePreviewSnapshot(
                directoryInfo.Name,
                path,
                "Folder",
                "Size: -",
                $"Modified: {directoryInfo.LastWriteTime:dd.MM.yyyy HH:mm}");
            return true;
        }

        if (!File.Exists(path))
        {
            return false;
        }

        var fileInfo = new FileInfo(path);
        var extension = Path.GetExtension(fileInfo.Name);
        var type = string.IsNullOrWhiteSpace(extension)
            ? "File"
            : $"{extension.ToUpperInvariant()} file";

        snapshot = new FilePreviewSnapshot(
            fileInfo.Name,
            path,
            $"Type: {type}",
            $"Size: {FormatFileSize(fileInfo.Length)}",
            $"Modified: {fileInfo.LastWriteTime:dd.MM.yyyy HH:mm}");
        return true;
    }

    private static string GetPreviewPath(SearchResultItem selectedItem)
    {
        if (!string.IsNullOrWhiteSpace(selectedItem.ResolvedTarget) &&
            Path.IsPathRooted(selectedItem.ResolvedTarget))
        {
            return selectedItem.ResolvedTarget;
        }

        if (!string.IsNullOrWhiteSpace(selectedItem.ActionValue) &&
            Path.IsPathRooted(selectedItem.ActionValue))
        {
            return selectedItem.ActionValue;
        }

        return string.Empty;
    }

    private static async Task<string> LoadFilePreviewSnippetAsync(
        string path,
        SearchResultKind resultKind,
        CancellationToken cancellationToken)
    {
        if (resultKind == SearchResultKind.Folder)
        {
            return await LoadFolderPreviewAsync(path, cancellationToken);
        }

        if (!IsTextPreviewExtension(path) || !File.Exists(path))
        {
            return string.Empty;
        }

        try
        {
            return await Task.Run(() =>
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
                var buffer = new char[PreviewTextReadChars];
                var read = reader.ReadBlock(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    return string.Empty;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var previewSnippet = new string(buffer, 0, read)
                    .Replace("\0", " ", StringComparison.Ordinal)
                    .Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Replace("\r", "\n", StringComparison.Ordinal)
                    .Trim();

                while (previewSnippet.Contains("\n\n\n", StringComparison.Ordinal))
                {
                    previewSnippet = previewSnippet.Replace("\n\n\n", "\n\n", StringComparison.Ordinal);
                }

                if (previewSnippet.Length <= PreviewSnippetMaxLength)
                {
                    return previewSnippet;
                }

                return $"{previewSnippet[..PreviewSnippetMaxLength].TrimEnd()}...";
            }, cancellationToken);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task<string> LoadFolderPreviewAsync(string path, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path))
        {
            return string.Empty;
        }

        try
        {
            return await Task.Run(() =>
            {
                var folderPreviewItems = new List<string>(FolderPreviewMaxItems);
                var filePreviewItems = new List<string>(FolderPreviewMaxItems);
                var folderCount = 0;
                var fileCount = 0;

                foreach (var directoryPath in Directory.EnumerateDirectories(path))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    folderCount++;

                    if (folderPreviewItems.Count < FolderPreviewMaxItems)
                    {
                        folderPreviewItems.Add(Path.GetFileName(directoryPath));
                    }
                }

                foreach (var filePath in Directory.EnumerateFiles(path))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    fileCount++;

                    if (filePreviewItems.Count < FolderPreviewMaxItems)
                    {
                        filePreviewItems.Add(Path.GetFileName(filePath));
                    }
                }

                var lines = new List<string>
                {
                    $"Folders: {folderCount}",
                    $"Files: {fileCount}",
                    string.Empty
                };

                foreach (var folder in folderPreviewItems
                             .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                             .Take(FolderPreviewMaxItems))
                {
                    lines.Add($"[D] {folder}");
                }

                var remainingSlots = Math.Max(0, FolderPreviewMaxItems - Math.Min(FolderPreviewMaxItems, folderPreviewItems.Count));
                foreach (var file in filePreviewItems
                             .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                             .Take(remainingSlots))
                {
                    lines.Add($"[F] {file}");
                }

                if (folderCount + fileCount > FolderPreviewMaxItems)
                {
                    lines.Add("...");
                }

                return string.Join(Environment.NewLine, lines).Trim();
            }, cancellationToken);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsTextPreviewExtension(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".log", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".csv", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".xml", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".yml", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".ini", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".config", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".xaml", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldUseLargeFilePreviewImage(SearchResultItem selectedItem, string previewPath)
    {
        if (selectedItem.Kind != SearchResultKind.File || !selectedItem.UsesThumbnailPreview)
        {
            return false;
        }

        var extension = Path.GetExtension(previewPath);
        return !string.IsNullOrWhiteSpace(extension) &&
               LargePreviewImageExtensions.Contains(extension);
    }

    private static string FormatFileSize(long sizeBytes)
    {
        if (sizeBytes < 1024)
        {
            return $"{sizeBytes} B";
        }

        if (sizeBytes < 1024 * 1024)
        {
            return $"{sizeBytes / 1024d:0.#} KB";
        }

        if (sizeBytes < 1024L * 1024 * 1024)
        {
            return $"{sizeBytes / (1024d * 1024):0.#} MB";
        }

        return $"{sizeBytes / (1024d * 1024 * 1024):0.#} GB";
    }

    private static void ReportException(string source, Exception exception)
    {
        Debug.WriteLine($"[SpotCont:ViewModel:{source}] {exception}");
    }

    private void RefreshSearchIconSource()
    {
        SearchIconSource = GetSearchIconSourcePath();
    }

    private static string GetSearchIconSourcePath()
    {
        return ThemeService.IsLightThemeEnabled()
            ? "pack://application:,,,/ServiceImage/Search_Icon.png"
            : "pack://application:,,,/ServiceImage/Search_Icon_Light.png";
    }

    private static void TryCancelAndDispose(CancellationTokenSource? cancellationTokenSource)
    {
        if (cancellationTokenSource is null)
        {
            return;
        }

        try
        {
            cancellationTokenSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            TryDispose(cancellationTokenSource);
        }
    }

    private static void TryDispose(CancellationTokenSource cancellationTokenSource)
    {
        try
        {
            cancellationTokenSource.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static bool CanSearchOnInternet(SearchResultItem item)
    {
        return !string.IsNullOrWhiteSpace(item.SearchInternetText) &&
               item.ActionKind != ResultActionKind.SearchWeb;
    }

    private static string GetOpenActionTitle(SearchResultItem item)
    {
        return item.Kind switch
        {
            SearchResultKind.Web => "Open site",
            SearchResultKind.Folder => "Open folder",
            SearchResultKind.SystemCommand => "Run command",
            SearchResultKind.Calculator => "Copy result",
            _ => "Open"
        };
    }

    private static string GetCopyActionTitle(SearchResultItem item)
    {
        return item.Kind switch
        {
            SearchResultKind.Web => "Copy link",
            SearchResultKind.Calculator => "Copy result",
            _ => "Copy path"
        };
    }

    private readonly record struct FilePreviewSnapshot(
        string Title,
        string Path,
        string Type,
        string Size,
        string Modified);
}
