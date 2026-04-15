using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpotCont.Models;

namespace SpotCont.Services;

public sealed class IconCacheService
{
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeNormal = 0x80;
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiUseFileAttributes = 0x000000010;
    private const int ShellIconSize = 32;
    private const int PreviewShellIconSize = 72;
    private const int PreviewThumbnailSize = 1024;

    private static readonly HashSet<string> ThumbnailExtensions = new(StringComparer.OrdinalIgnoreCase)
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

    private static readonly HttpClient HttpClient = CreateHttpClient();

    private readonly ConcurrentDictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ImageSource?> _previewIconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ImageSource?> _fallbackCache = new(StringComparer.OrdinalIgnoreCase);

    public void InvalidateThemeDependentCache()
    {
        _iconCache.Clear();
        _previewIconCache.Clear();
        _fallbackCache.Clear();
    }

    public ImageSource? GetInitialIcon(SearchResultItem item)
    {
        var cacheKey = BuildCacheKey(item, "list");
        return _iconCache.TryGetValue(cacheKey, out var icon)
            ? icon
            : GetFallbackIcon(item.Kind);
    }

    public ImageSource? GetInitialPreviewImage(SearchResultItem item)
    {
        var cacheKey = BuildCacheKey(item, "preview");
        return _previewIconCache.TryGetValue(cacheKey, out var icon)
            ? icon
            : GetFallbackIcon(item.Kind, PreviewShellIconSize);
    }

    public async Task<ImageSource?> GetIconAsync(SearchResultItem item, CancellationToken cancellationToken = default)
    {
        var cacheKey = BuildCacheKey(item, "list");
        if (_iconCache.TryGetValue(cacheKey, out var cachedIcon))
        {
            return cachedIcon;
        }

        ImageSource? actualIcon = null;

        if (item.UsesThumbnailPreview &&
            !string.IsNullOrWhiteSpace(item.ResolvedTarget) &&
            File.Exists(item.ResolvedTarget) &&
            ThumbnailExtensions.Contains(Path.GetExtension(item.ResolvedTarget)))
        {
            actualIcon = await Task.Run(() => LoadImageThumbnail(item.ResolvedTarget, 80), cancellationToken);
        }
        else if (item.Kind == SearchResultKind.Web)
        {
            actualIcon = await LoadWebsiteIconAsync(item.ActionValue, cancellationToken);
        }
        else if (item.UsesShellIcon)
        {
            var iconPath = GetShellIconPath(item);
            if (!string.IsNullOrWhiteSpace(iconPath))
            {
                actualIcon = await Task.Run(() => LoadShellIcon(iconPath, item.Kind, ShellIconSize), cancellationToken);
            }
        }

        var resolvedIcon = actualIcon ?? GetFallbackIcon(item.Kind);
        _iconCache.TryAdd(cacheKey, resolvedIcon);
        return resolvedIcon;
    }

    public async Task<ImageSource?> GetPreviewImageAsync(SearchResultItem item, CancellationToken cancellationToken = default)
    {
        var cacheKey = BuildCacheKey(item, "preview");
        if (_previewIconCache.TryGetValue(cacheKey, out var cachedPreviewImage))
        {
            return cachedPreviewImage;
        }

        ImageSource? previewImage = null;

        if (item.UsesThumbnailPreview &&
            !string.IsNullOrWhiteSpace(item.ResolvedTarget) &&
            File.Exists(item.ResolvedTarget) &&
            ThumbnailExtensions.Contains(Path.GetExtension(item.ResolvedTarget)))
        {
            previewImage = await Task.Run(
                () => LoadImageThumbnail(item.ResolvedTarget, PreviewThumbnailSize),
                cancellationToken);
        }
        else if (item.UsesShellIcon)
        {
            var iconPath = GetShellIconPath(item);
            if (!string.IsNullOrWhiteSpace(iconPath))
            {
                previewImage = await Task.Run(
                    () => LoadShellIcon(iconPath, item.Kind, PreviewShellIconSize),
                    cancellationToken);
            }
        }

        var resolvedPreviewImage = previewImage ?? GetFallbackIcon(item.Kind, PreviewShellIconSize);
        _previewIconCache.TryAdd(cacheKey, resolvedPreviewImage);
        return resolvedPreviewImage;
    }

    private ImageSource? GetFallbackIcon(SearchResultKind kind, int iconSize = ShellIconSize)
    {
        var usesLightTheme = ThemeService.IsLightThemeEnabled();
        var cacheKey = $"{kind}|{iconSize}|{(usesLightTheme ? "light" : "dark")}";

        return _fallbackCache.GetOrAdd(cacheKey, _ => LoadFallbackIcon(kind, usesLightTheme, iconSize));
    }

    private static ImageSource? LoadFallbackIcon(SearchResultKind kind, bool usesLightTheme, int iconSize)
    {
        var baseResourcePath = kind switch
        {
            SearchResultKind.Application => "ServiceImage/App_Icon.png",
            SearchResultKind.File => "ServiceImage/File_Icon.png",
            SearchResultKind.Folder => "ServiceImage/Folder_Icon.png",
            SearchResultKind.Web => "ServiceImage/Web_Icon.png",
            SearchResultKind.Calculator => "ServiceImage/System_Icon.png",
            SearchResultKind.SystemCommand => "ServiceImage/System_Icon.png",
            SearchResultKind.Command => "ServiceImage/System_Icon.png",
            _ => "ServiceImage/Search_Icon.png"
        };

        var lightResourcePath = BuildLightResourcePath(baseResourcePath);
        var preferredPath = usesLightTheme ? baseResourcePath : lightResourcePath;
        var fallbackPath = usesLightTheme ? lightResourcePath : baseResourcePath;

        return TryLoadBitmap(preferredPath, iconSize) ?? TryLoadBitmap(fallbackPath, iconSize);
    }

    private static string BuildLightResourcePath(string resourcePath)
    {
        var extension = Path.GetExtension(resourcePath);
        var pathWithoutExtension = resourcePath[..^extension.Length];
        return $"{pathWithoutExtension}_Light{extension}";
    }

    private static ImageSource? TryLoadBitmap(string resourcePath, int iconSize)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri($"pack://application:,,,/{resourcePath}", UriKind.Absolute);
            bitmap.DecodePixelWidth = iconSize;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetShellIconPath(SearchResultItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.ResolvedTarget) &&
            Path.IsPathRooted(item.ResolvedTarget) &&
            (File.Exists(item.ResolvedTarget) || Directory.Exists(item.ResolvedTarget)))
        {
            return item.ResolvedTarget;
        }

        if (!string.IsNullOrWhiteSpace(item.ActionValue) &&
            Path.IsPathRooted(item.ActionValue) &&
            (File.Exists(item.ActionValue) || Directory.Exists(item.ActionValue)))
        {
            return item.ActionValue;
        }

        return null;
    }

    private static ImageSource? LoadShellIcon(string path, SearchResultKind kind, int iconSize)
    {
        try
        {
            var flags = ShgfiIcon;
            var fileAttributes = 0u;

            if (!Directory.Exists(path) && !File.Exists(path))
            {
                flags |= ShgfiUseFileAttributes;
                fileAttributes = kind == SearchResultKind.Folder
                    ? FileAttributeDirectory
                    : FileAttributeNormal;
            }

            var fileInfo = new ShFileInfo();
            var result = SHGetFileInfo(path, fileAttributes, out fileInfo, (uint)Marshal.SizeOf<ShFileInfo>(), flags);
            if (result == IntPtr.Zero || fileInfo.hIcon == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var imageSource = Imaging.CreateBitmapSourceFromHIcon(
                    fileInfo.hIcon,
                    System.Windows.Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(iconSize, iconSize));
                imageSource.Freeze();
                return imageSource;
            }
            finally
            {
                DestroyIcon(fileInfo.hIcon);
            }
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? LoadImageThumbnail(string path, int decodePixelWidth)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bitmap.DecodePixelWidth = decodePixelWidth;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ImageSource?> LoadWebsiteIconAsync(string target, CancellationToken cancellationToken)
    {
        if (!TryGetWebsiteUri(target, out var siteUri))
        {
            return null;
        }

        var authority = siteUri.GetLeftPart(UriPartial.Authority);
        var candidates = new[]
        {
            new Uri($"{authority.TrimEnd('/')}/favicon.ico", UriKind.Absolute),
            new Uri($"https://www.google.com/s2/favicons?sz=64&domain_url={Uri.EscapeDataString(authority)}", UriKind.Absolute)
        };

        foreach (var candidate in candidates)
        {
            var icon = await TryDownloadBitmapAsync(candidate, cancellationToken);
            if (icon is not null)
            {
                return icon;
            }
        }

        return null;
    }

    private static async Task<ImageSource?> TryDownloadBitmapAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await HttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length == 0)
            {
                return null;
            }

            using var memoryStream = new MemoryStream(bytes, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = ShellIconSize;
            bitmap.StreamSource = memoryStream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetWebsiteUri(string value, out Uri uri)
    {
        uri = null!;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var absoluteUri) &&
            (absoluteUri.Scheme == Uri.UriSchemeHttp || absoluteUri.Scheme == Uri.UriSchemeHttps))
        {
            uri = absoluteUri;
            return true;
        }

        if (!value.Contains('.') || value.Contains(' '))
        {
            return false;
        }

        if (Uri.TryCreate($"https://{value}", UriKind.Absolute, out var implicitUri))
        {
            uri = implicitUri;
            return true;
        }

        return false;
    }

    private static string BuildCacheKey(SearchResultItem item, string profile)
    {
        var themeToken = ThemeService.IsLightThemeEnabled() ? "light" : "dark";

        if (item.Kind == SearchResultKind.Web && TryGetWebsiteUri(item.ActionValue, out var siteUri))
        {
            return $"{profile}|{item.Kind}|{siteUri.Host}|{themeToken}";
        }

        if (!string.IsNullOrWhiteSpace(item.ResolvedTarget))
        {
            return $"{profile}|{item.Kind}|{item.ResolvedTarget}|{themeToken}";
        }

        return $"{profile}|{item.Kind}|{item.ActionValue}|{themeToken}";
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(3)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SpotCont/2.0");
        return client;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        out ShFileInfo psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public IntPtr iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }
}
