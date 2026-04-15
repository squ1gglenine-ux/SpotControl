using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SpotCont.Services;

public sealed class ThemeService : IDisposable
{
    private const string PersonalizeRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string DwmRegistryPath = @"Software\Microsoft\Windows\DWM";
    private const string ExplorerAccentRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";

    private readonly Application _application;
    private readonly DispatcherTimer _themePollingTimer;
    private bool _lastUsesLightTheme;
    private Color _lastAccentColor;
    private bool _hasThemeSnapshot;
    private bool _isStarted;

    public event EventHandler? ThemeChanged;

    public ThemeService(Application application)
    {
        _application = application;
        _themePollingTimer = new DispatcherTimer(DispatcherPriority.Background, _application.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(280)
        };
        _themePollingTimer.Tick += ThemePollingTimer_Tick;
    }

    public void Start()
    {
        if (_isStarted)
        {
            return;
        }

        _isStarted = true;
        ApplyCurrentTheme(force: true);
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
        _themePollingTimer.Start();
    }

    public void Dispose()
    {
        if (!_isStarted)
        {
            return;
        }

        _themePollingTimer.Stop();
        SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
        _isStarted = false;
    }

    public void ApplyCurrentTheme()
    {
        ApplyCurrentTheme(force: true);
    }

    public static bool IsLightThemeEnabled()
    {
        return ReadUsesLightTheme();
    }

    private void ApplyCurrentTheme(bool force)
    {
        var usesLightTheme = ReadUsesLightTheme();
        var accentColor = ReadAccentColor();
        var hasChanged =
            !_hasThemeSnapshot ||
            _lastUsesLightTheme != usesLightTheme ||
            _lastAccentColor != accentColor;

        if (!force &&
            !hasChanged)
        {
            return;
        }

        _lastUsesLightTheme = usesLightTheme;
        _lastAccentColor = accentColor;
        _hasThemeSnapshot = true;

        var palette = CreatePalette(usesLightTheme, accentColor);
        ApplyPalette(_application.Resources, palette);

        if (hasChanged)
        {
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SystemEvents_UserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.Color or UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle)
        {
            if (_application.Dispatcher.CheckAccess())
            {
                ApplyCurrentTheme(force: false);
            }
            else
            {
                _application.Dispatcher.BeginInvoke(() => ApplyCurrentTheme(force: false));
            }
        }
    }

    private void ThemePollingTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            ApplyCurrentTheme(force: false);
        }
        catch
        {
        }
    }

    private static bool ReadUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeRegistryPath);

            if (TryReadDword(key, "AppsUseLightTheme", out var appsUseLightTheme))
            {
                return appsUseLightTheme != 0;
            }

            if (TryReadDword(key, "SystemUsesLightTheme", out var systemUsesLightTheme))
            {
                return systemUsesLightTheme != 0;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static Color ReadAccentColor()
    {
        if (TryReadDwmAccentColor(out var dwmAccentColor))
        {
            return dwmAccentColor;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(DwmRegistryPath);
            if (TryReadColorFromAbgr(key, "AccentColor", out var accentColor))
            {
                return accentColor;
            }

            if (TryReadColorFromAbgr(key, "ColorizationColor", out var colorizationColor))
            {
                return colorizationColor;
            }
        }
        catch
        {
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ExplorerAccentRegistryPath);
            if (TryReadColorFromAbgr(key, "AccentColorMenu", out var menuAccentColor))
            {
                return menuAccentColor;
            }

            if (TryReadColorFromAbgr(key, "StartColorMenu", out var startMenuAccentColor))
            {
                return startMenuAccentColor;
            }
        }
        catch
        {
        }

        return Color.FromRgb(0x3A, 0x82, 0xF7);
    }

    private static ThemePalette CreatePalette(bool isLightTheme, Color accent)
    {
        accent = Color.FromRgb(accent.R, accent.G, accent.B);

        if (isLightTheme)
        {
            var background = Blend(Color.FromRgb(0xF7, 0xF9, 0xFC), accent, 0.04);
            var border = Blend(Color.FromRgb(0xD7, 0xDF, 0xE8), accent, 0.16);
            var iconBackground = Blend(Color.FromRgb(0xFF, 0xFF, 0xFF), accent, 0.09);

            return new ThemePalette(
                background,
                border,
                Blend(border, accent, 0.10),
                Blend(background, accent, 0.08),
                Blend(background, accent, 0.14),
                accent,
                Color.FromRgb(0x10, 0x18, 0x24),
                Color.FromRgb(0x51, 0x5F, 0x70),
                Color.FromRgb(0x6A, 0x79, 0x8A),
                iconBackground,
                Blend(border, accent, 0.10),
                Blend(background, Color.FromRgb(0xFA, 0xFB, 0xFD), 0.5),
                border,
                Blend(background, accent, 0.18));
        }

        var darkBackground = Blend(Color.FromRgb(0x0D, 0x10, 0x15), accent, 0.08);
        var darkBorder = Blend(Color.FromRgb(0x18, 0x1E, 0x27), accent, 0.18);
        var darkIconBackground = Blend(Color.FromRgb(0x13, 0x17, 0x1E), accent, 0.08);

        return new ThemePalette(
            darkBackground,
            darkBorder,
            Blend(darkBorder, accent, 0.08),
            Blend(darkBackground, accent, 0.11),
            Blend(darkBackground, accent, 0.18),
            accent,
            Color.FromRgb(0xF5, 0xF7, 0xFB),
            Color.FromRgb(0xB1, 0xB8, 0xC4),
            Color.FromRgb(0x8C, 0x94, 0xA2),
            darkIconBackground,
            Blend(darkBorder, accent, 0.14),
            Blend(darkBackground, Color.FromRgb(0x0A, 0x0C, 0x10), 0.35),
            darkBorder,
            Blend(darkBackground, accent, 0.22));
    }

    private static void ApplyPalette(ResourceDictionary resources, ThemePalette palette)
    {
        SetBrushColor(resources, "WindowBackgroundBrush", palette.WindowBackground);
        SetBrushColor(resources, "WindowBorderBrush", palette.WindowBorder);
        SetBrushColor(resources, "SearchBarBorderBrush", palette.SearchBarBorder);
        SetBrushColor(resources, "HoverBrush", palette.Hover);
        SetBrushColor(resources, "SelectedBrush", palette.Selected);
        SetBrushColor(resources, "AccentBrush", palette.Accent);
        SetBrushColor(resources, "PrimaryTextBrush", palette.PrimaryText);
        SetBrushColor(resources, "SecondaryTextBrush", palette.SecondaryText);
        SetBrushColor(resources, "TertiaryTextBrush", palette.TertiaryText);
        SetBrushColor(resources, "ResultIconBackgroundBrush", palette.IconBackground);
        SetBrushColor(resources, "ResultIconBorderBrush", palette.IconBorder);
        SetBrushColor(resources, "ContextMenuBackgroundBrush", palette.ContextMenuBackground);
        SetBrushColor(resources, "ContextMenuBorderBrush", palette.ContextMenuBorder);
        SetBrushColor(resources, "ContextMenuHoverBrush", palette.ContextMenuHover);
    }

    private static void SetBrushColor(ResourceDictionary resources, string key, Color color)
    {
        if (resources.Contains(key) &&
            resources[key] is SolidColorBrush brush)
        {
            if (brush.IsFrozen)
            {
                var mutableBrush = brush.Clone();
                mutableBrush.Color = color;
                resources[key] = mutableBrush;
                return;
            }

            if (brush.Color == color)
            {
                return;
            }

            brush.Color = color;
            return;
        }

        resources[key] = new SolidColorBrush(color);
    }

    private static bool TryReadDword(RegistryKey? key, string valueName, out int value)
    {
        value = 0;
        var rawValue = key?.GetValue(valueName);
        switch (rawValue)
        {
            case int intValue:
                value = intValue;
                return true;
            case long longValue:
                value = unchecked((int)longValue);
                return true;
            default:
                return false;
        }
    }

    private static bool TryReadColorFromAbgr(RegistryKey? key, string valueName, out Color color)
    {
        color = default;
        if (!TryReadDword(key, valueName, out var value))
        {
            return false;
        }

        color = FromAbgr(unchecked((uint)value));
        return true;
    }

    private static bool TryReadDwmAccentColor(out Color color)
    {
        color = default;

        try
        {
            var result = DwmGetColorizationColor(out var rawColor, out _);
            if (result != 0)
            {
                return false;
            }

            color = FromArgb(rawColor);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Color Blend(Color background, Color overlay, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);

        return Color.FromRgb(
            (byte)(background.R + (overlay.R - background.R) * amount),
            (byte)(background.G + (overlay.G - background.G) * amount),
            (byte)(background.B + (overlay.B - background.B) * amount));
    }

    private static Color FromAbgr(uint value)
    {
        var blue = (byte)((value >> 16) & 0xFF);
        var green = (byte)((value >> 8) & 0xFF);
        var red = (byte)(value & 0xFF);
        return Color.FromRgb(red, green, blue);
    }

    private static Color FromArgb(uint value)
    {
        var red = (byte)((value >> 16) & 0xFF);
        var green = (byte)((value >> 8) & 0xFF);
        var blue = (byte)(value & 0xFF);
        return Color.FromRgb(red, green, blue);
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmGetColorizationColor(out uint pcrColorization, out bool pfOpaqueBlend);

    private sealed record ThemePalette(
        Color WindowBackground,
        Color WindowBorder,
        Color SearchBarBorder,
        Color Hover,
        Color Selected,
        Color Accent,
        Color PrimaryText,
        Color SecondaryText,
        Color TertiaryText,
        Color IconBackground,
        Color IconBorder,
        Color ContextMenuBackground,
        Color ContextMenuBorder,
        Color ContextMenuHover);
}
