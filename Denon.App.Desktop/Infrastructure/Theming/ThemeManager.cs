using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Denon.App.Desktop.Controls;
using Microsoft.Win32;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace Denon.App.Desktop.Infrastructure.Theming;

/// <summary>A built-in theme: a palette of color tokens (docs/theming.md).</summary>
public sealed record ThemeInfo(string Id, string Name, bool IsDark, Uri PaletteUri);

/// <summary>
/// A skin (docs/theming.md): its token dictionary (fonts, type scale, radii, spacing and size scales) and its meter
/// materials. Applied live; any skin works with any theme.
/// </summary>
public sealed record SkinInfo(string Id, string Name, string Hint, Uri TokensUri, MeterPalette Meters);

/// <summary>
/// Applies themes and skins live by replacing slots of the theme stack in <c>App.xaml</c>:
/// 0 palette, 1 brushes (theme), 2 skin tokens (skin), 3 icons, 4 control styles. See docs/theming.md.
/// </summary>
public static class ThemeManager
{
    /// <summary>The theme setting that follows the Windows app mode (light or dark).</summary>
    public const string SystemThemeId = "system";

    public const string DefaultSkinId = "default";

    private const int PaletteSlot = 0;
    private const int BrushesSlot = 1;
    private const int SkinSlot = 2;

    // Registers the pack:// URI scheme, which only WPF's own start-up does otherwise (unit tests have none).
    private static readonly string PackScheme = System.IO.Packaging.PackUriHelper.UriSchemePack;
    private static readonly Uri BrushesUri = Pack("Themes/Brushes.xaml");
    private static string _setting = SystemThemeId;
    private static bool _listeningToWindows;

    public static IReadOnlyList<ThemeInfo> Themes { get; } =
    [
        new("dark", "Dark", IsDark: true, Pack("Themes/Palettes/Dark.xaml")),
        new("light", "Light", IsDark: false, Pack("Themes/Palettes/Light.xaml")),
    ];

    public static IReadOnlyList<SkinInfo> Skins { get; } =
    [
        new(DefaultSkinId, "Default", "The standard look", Pack("Themes/Skins/Default.xaml"), MeterPalette.Default),
        new("hifi", "Hi-Fi", "Rack receiver: square corners, display type, backlit meters", Pack("Themes/Skins/HiFi.xaml"), MeterPalette.HiFi),
        new("compact", "Compact", "Smaller type and spacing: more fits on screen", Pack("Themes/Skins/Compact.xaml"), MeterPalette.Default),
        new("large", "Large", "Larger type, icons and spacing: readable from the sofa", Pack("Themes/Skins/Large.xaml"), MeterPalette.Default),
    ];

    /// <summary>The theme on screen now (App.xaml starts with Dark).</summary>
    public static ThemeInfo Current { get; private set; } = Themes[0];

    public static SkinInfo CurrentSkin { get; private set; } = Skins[0];

    /// <summary>After a theme switch, on the UI thread. Code that draws with theme colors redraws here.</summary>
    public static event EventHandler? ThemeChanged;

    /// <summary>After a skin switch, on the UI thread. Code that draws with skin fonts or meter materials redraws here.</summary>
    public static event EventHandler? SkinChanged;

    /// <summary>The theme for a setting: <c>system</c> follows Windows; an unknown id gives Dark.</summary>
    public static ThemeInfo Resolve(string? setting) =>
        string.Equals(setting, SystemThemeId, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(setting)
            ? Themes.First(t => t.IsDark != WindowsAppsUseLightTheme())
            : Themes.FirstOrDefault(t => string.Equals(t.Id, setting, StringComparison.OrdinalIgnoreCase)) ?? Themes[0];

    /// <summary>
    /// Applies a theme setting (<c>system</c>, <c>dark</c>, <c>light</c>) live. With <c>system</c> the app follows
    /// later changes of the Windows app mode too. Call on the UI thread.
    /// </summary>
    public static void ApplyTheme(string? setting)
    {
        _setting = string.IsNullOrWhiteSpace(setting) ? SystemThemeId : setting.Trim().ToLowerInvariant();
        ListenToWindows(_setting == SystemThemeId);
        Apply(Resolve(_setting));
    }

    /// <summary>The skin for a setting; an unknown id gives Default.</summary>
    public static SkinInfo ResolveSkin(string? skinId) =>
        Skins.FirstOrDefault(s => string.Equals(s.Id, skinId, StringComparison.OrdinalIgnoreCase)) ?? Skins[0];

    /// <summary>
    /// Applies a skin live: the token dictionary in slot 2 (views refer to its tokens with DynamicResource,
    /// {skin:Space} and {skin:Size}) and the meter materials. Call on the UI thread.
    /// </summary>
    public static void ApplySkin(string? skinId)
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        // Plugins bind to the skin scales by this name (no reference to the app).
        app.Resources[SkinMetrics.ResourceKey] = SkinMetrics.Current;

        var skin = ResolveSkin(skinId);
        if (skin == CurrentSkin)
        {
            return;
        }

        app.Resources.MergedDictionaries[SkinSlot] = new ResourceDictionary { Source = skin.TokensUri };
        MeterPalette.Current = skin.Meters;
        CurrentSkin = skin;
        SkinMetrics.Current.Reload();
        SkinChanged?.Invoke(null, EventArgs.Empty);
    }

    private static void Apply(ThemeInfo theme)
    {
        if (theme == Current || Application.Current is not { } app)
        {
            return;
        }

        var merged = app.Resources.MergedDictionaries;
        var oldBrushes = BrushKeys(merged[BrushesSlot]);

        // Palette first: the new brushes look their colors up while they load.
        merged[PaletteSlot] = new ResourceDictionary { Source = theme.PaletteUri };
        merged[BrushesSlot] = new ResourceDictionary { Source = BrushesUri };
        Current = theme;

        foreach (Window window in app.Windows)
        {
            Refresh(window, oldBrushes);
        }

        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Every brush of a brushes dictionary and its key (the brushes are frozen and shared).</summary>
    private static Dictionary<Brush, string> BrushKeys(ResourceDictionary brushes)
    {
        var map = new Dictionary<Brush, string>(ReferenceEqualityComparer.Instance);
        foreach (var key in brushes.Keys.OfType<string>())
        {
            if (brushes[key] is Brush brush)
            {
                map.TryAdd(brush, key);
            }
        }

        return map;
    }

    /// <summary>
    /// Brings what DynamicResource does not reach to the new theme:
    /// <list type="bullet">
    /// <item>bindings that give an element a brush or color are read again: view models and converters build brushes
    /// from token names when they are read, so they return the new theme's brush;</item>
    /// <item>an element that holds an old theme brush as a plain value (code that assigned <c>Foreground = …</c>,
    /// for example a plugin page built in code) gets a live reference to the same token instead.</item>
    /// </list>
    /// </summary>
    private static void Refresh(DependencyObject root, IReadOnlyDictionary<Brush, string> oldBrushes)
    {
        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (!visited.Add(node))
            {
                continue;
            }

            var rebind = new List<(DependencyProperty Property, string Key)>();
            var values = node.GetLocalValueEnumerator();
            while (values.MoveNext())
            {
                var entry = values.Current;
                if (entry.Value is BindingExpressionBase binding && IsColorProperty(entry.Property.PropertyType))
                {
                    binding.UpdateTarget();
                }
                else if (entry.Value is Brush brush && oldBrushes.TryGetValue(brush, out var key) && !entry.Property.ReadOnly)
                {
                    rebind.Add((entry.Property, key)); // not while enumerating the local values
                }
            }

            // Bindings that come from a template or style are not local values: check the usual brush properties.
            foreach (var property in TemplateBrushProperties)
            {
                BindingOperations.GetBindingExpressionBase(node, property)?.UpdateTarget();
            }

            foreach (var (property, key) in rebind)
            {
                switch (node)
                {
                    case FrameworkElement element:
                        element.SetResourceReference(property, key);
                        break;
                    case FrameworkContentElement content:
                        content.SetResourceReference(property, key);
                        break;
                }
            }

            if (node is Visual or System.Windows.Media.Media3D.Visual3D)
            {
                for (var i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--)
                {
                    pending.Push(VisualTreeHelper.GetChild(node, i));
                }
            }

            // Logical children reach what the visual tree does not: Runs in a TextBlock, popups, collapsed content.
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            {
                pending.Push(child);
            }
        }
    }

    private static readonly DependencyProperty[] TemplateBrushProperties =
    [
        .. new[]
        {
            System.Windows.Controls.Control.ForegroundProperty, System.Windows.Controls.Control.BackgroundProperty,
            System.Windows.Controls.Control.BorderBrushProperty, System.Windows.Controls.Border.BackgroundProperty,
            System.Windows.Controls.Border.BorderBrushProperty, System.Windows.Controls.Panel.BackgroundProperty,
            System.Windows.Shapes.Shape.FillProperty, System.Windows.Shapes.Shape.StrokeProperty,
            System.Windows.Controls.TextBlock.ForegroundProperty, System.Windows.Controls.TextBlock.BackgroundProperty,
            System.Windows.Documents.TextElement.ForegroundProperty,
        }.Distinct(),
    ];

    private static bool IsColorProperty(Type type) => typeof(Brush).IsAssignableFrom(type) || type == typeof(Color);

    /// <summary>Windows "Choose your app mode": true for Light. Dark when the value cannot be read.</summary>
    public static bool WindowsAppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return false;
        }
    }

    private static void ListenToWindows(bool listen)
    {
        if (listen == _listeningToWindows)
        {
            return;
        }

        _listeningToWindows = listen;
        if (listen)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
        else
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        }
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General || _setting != SystemThemeId)
        {
            return;
        }

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_setting == SystemThemeId)
            {
                Apply(Resolve(SystemThemeId));
            }
        });
    }

    private static Uri Pack(string path) => new($"pack://application:,,,/{path}", UriKind.Absolute);
}
