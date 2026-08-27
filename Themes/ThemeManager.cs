using System.IO;
using System.Windows;
using System.Windows.Media;
using SwitchBoard.Data;

namespace SwitchBoard.Themes;

public sealed class ThemeManager(AppDataPaths paths) : IThemeManager
{
    private const string ThemeMarker = "SwitchBoard.Theme.Dictionary";
    private const double DefaultHoverIntensity = 78d;
    private const double MaxInteractiveHoverScale = 0.8d;
    private readonly IReadOnlyList<ThemeDefinition> _availableThemes =
    [
        Create(ThemeIds.Graphite, "Theme.GraphiteGlass", "GraphiteTheme.xaml"),
        Create(ThemeIds.Dark, "Theme.Dark", "DarkTheme.xaml"),
        Create(ThemeIds.DarkBlue, "Theme.DarkBlue", "DarkBlueTheme.xaml"),
        Create(ThemeIds.Light, "Theme.Light", "LightTheme.xaml"),
        Create(ThemeIds.MidnightTools, "Theme.MidnightTools", "MidnightToolsTheme.xaml"),
        Create(ThemeIds.NordicFrost, "Theme.NordicFrost", "NordicFrostTheme.xaml"),
        Create(ThemeIds.VioletDusk, "Theme.VioletDusk", "VioletDuskTheme.xaml"),
        Create(ThemeIds.WarmAmber, "Theme.WarmAmber", "WarmAmberTheme.xaml"),
        Create(ThemeIds.SolarDepths, "Theme.SolarDepths", "SolarDepthsTheme.xaml"),
        Create(ThemeIds.OledBlack, "Theme.OledBlack", "OledBlackTheme.xaml")
    ];

    public IReadOnlyList<ThemeDefinition> AvailableThemes => _availableThemes;
    public string CurrentThemeId { get; private set; } = ThemeIds.Graphite;

    public string ApplyTheme(string? themeId, CustomThemeSettings? customTheme = null)
    {
        var theme = _availableThemes.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, themeId, StringComparison.OrdinalIgnoreCase));
        ResourceDictionary dictionary;
        string appliedId;
        if (customTheme is not null)
        {
            dictionary = CreateCustomDictionary(customTheme);
            appliedId = string.IsNullOrWhiteSpace(themeId) ? CustomThemeDefinition.CreateId() : themeId;
        }
        else
        {
            theme ??= _availableThemes[0];
            dictionary = new ResourceDictionary { Source = theme.ResourceUri };
            CompleteBackgroundContract(dictionary);
            if (!dictionary.Contains("CardSurfaceBrush"))
                dictionary["CardSurfaceBrush"] = dictionary["ElevatedSurfaceBrush"];
            CompleteSemanticContrastContract(dictionary);
            if (!dictionary.Contains("IconForeground")) dictionary["IconForeground"] = dictionary["IconPrimary"];
            dictionary[ThemeMarker] = true;
            appliedId = theme.Id;
        }

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        foreach (var existing in dictionaries.Where(IsThemeDictionary).ToList()) dictionaries.Remove(existing);
        dictionaries.Insert(0, dictionary);
        CurrentThemeId = appliedId;
        return appliedId;
    }

    public string ApplyTemporary(string draftThemeId, CustomThemeSettings draft) =>
        ApplyTheme(draftThemeId, draft.Clone());

    public CustomThemeSettings CreateEditableCopy(string builtInThemeId)
    {
        var theme = _availableThemes.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, builtInThemeId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown built-in theme '{builtInThemeId}'.", nameof(builtInThemeId));
        var dictionary = new ResourceDictionary { Source = theme.ResourceUri };
        string Read(string key, string fallback)
        {
            if (dictionary[key] is Brush brush)
                return RepresentativeColor(brush, Parse(fallback, fallback)).ToString();
            return fallback;
        }
        return new CustomThemeSettings
        {
            Background = Read("BackgroundBrush", "#FF11141B"),
            Panel = Read("SurfaceBrush", "#F01A1F29"),
            Card = Read("CardSurfaceBrush", Read("ElevatedSurfaceBrush", "#F0232935")),
            Elevated = Read("ElevatedSurfaceBrush", "#FF2B3240"),
            Border = Read("BorderBrush", "#52FFFFFF"),
            PrimaryText = Read("TextPrimaryBrush", "#FFF5F7FA"),
            SecondaryText = Read("TextSecondaryBrush", "#FFB5BDCA"),
            Accent = Read("AccentBrush", "#FF72A7FF"),
            Hover = Read("HoverBrush", "#3372A7FF"),
            HoverIntensity = DefaultHoverIntensity,
            Selection = Read("SelectedBrush", "#5572A7FF"),
            PrimaryButtonBackground = Read("PrimaryButtonBackground", "#FF72A7FF"),
            PrimaryButtonForeground = "auto",
            // These legacy overrides remain in the persisted model, but newly created
            // themes derive buttons and menus from the shared elevated surface.
            SecondaryButtonBackground = "auto",
            SecondaryButtonForeground = "auto",
            IconForeground = "auto",
            MenuBackground = "auto",
            MenuForeground = "auto",
            MenuHoverBackground = "auto",
            SurfaceOpacity = RepresentativeColor(dictionary["SurfaceBrush"] as Brush, Colors.White).A / 255d,
            CategoriesPanelOpacity = RepresentativeColor(dictionary["SurfaceBrush"] as Brush, Colors.White).A / 255d,
            ProfilesPanelOpacity = RepresentativeColor(dictionary["SurfaceBrush"] as Brush, Colors.White).A / 255d,
            ProfileEditorPanelOpacity = RepresentativeColor(dictionary["SurfaceBrush"] as Brush, Colors.White).A / 255d,
            ActivityPanelOpacity = RepresentativeColor(dictionary["SurfaceBrush"] as Brush, Colors.White).A / 255d
        };
    }

    private ResourceDictionary CreateCustomDictionary(CustomThemeSettings settings)
    {
        settings.NormalizeLegacy();
        var background = Parse(settings.Background, "#FF11141B");
        var surfaceOpacity = Math.Clamp(settings.SurfaceOpacity, 0, 1);
        var panelBase = Parse(settings.Panel, "#F01A1F29");
        var cardBase = Parse(settings.Card, "#F0232935");
        var elevatedBase = Parse(settings.Elevated, "#FF2B3240");
        var panel = ApplySurfaceOpacity(panelBase, surfaceOpacity);
        var card = ApplySurfaceOpacity(cardBase, surfaceOpacity);
        var elevated = ApplySurfaceOpacity(elevatedBase, surfaceOpacity);
        var categoriesPanel = ApplySurfaceOpacity(panelBase, settings.CategoriesPanelOpacity);
        var profilesPanel = ApplySurfaceOpacity(panelBase, settings.ProfilesPanelOpacity);
        var profileEditorPanel = ApplySurfaceOpacity(panelBase, settings.ProfileEditorPanelOpacity);
        // Activity is a primary application panel. Its independent opacity may differ,
        // but its base color must remain the shared panel color.
        var activityPanel = ApplySurfaceOpacity(panelBase, settings.ActivityPanelOpacity);
        var border = Parse(settings.Border, "#52FFFFFF");
        var primaryText = EnsureReadable(Parse(settings.PrimaryText, "#FFF5F7FA"), background);
        var secondaryText = EnsureReadable(Parse(settings.SecondaryText, "#FFB5BDCA"), background);
        var accent = Parse(settings.Accent, "#FF72A7FF");
        var hover = Parse(settings.Hover, "#3372A7FF");
        var selected = Parse(settings.Selection, "#5572A7FF");
        var primaryButton = Parse(settings.PrimaryButtonBackground, "#FF72A7FF");
        // Buttons, menus and popup surfaces all derive from their semantic bases. The
        // old per-control settings are intentionally not used here; they are retained
        // only so old custom-theme files can still be read and written safely.
        var secondaryButton = elevated;
        // Popup/menu surfaces must be opaque so glass panels behind them cannot bleed through.
        var popupBackground = WithAlpha(elevatedBase, 1);
        var primaryHover = Adjust(primaryButton, 1.12);
        var primaryPressed = Adjust(primaryButton, 0.82);
        var primaryDisabled = Blend(primaryButton, background, 0.65);
        var dictionary = new ResourceDictionary
        {
            [ThemeMarker] = true,
            ["BackgroundBrush"] = Brush(background),
            ["SurfaceBrush"] = Brush(panel),
            ["CategoriesSurfaceBrush"] = Brush(categoriesPanel),
            ["ProfilesSurfaceBrush"] = Brush(profilesPanel),
            ["ProfileEditorSurfaceBrush"] = Brush(profileEditorPanel),
            ["ActivitySurfaceBrush"] = Brush(activityPanel),
            ["CardSurfaceBrush"] = Brush(card),
            ["ElevatedSurfaceBrush"] = Brush(elevated),
            ["InputBackgroundBrush"] = Brush(Blend(elevatedBase, background, 0.35)),
            ["SettingsRowBackgroundBrush"] = Brush(CreateSettingsRowBackground(background, elevatedBase, panelBase)),
            ["PopupBackgroundBrush"] = Brush(popupBackground),
            ["BorderBrush"] = Brush(border),
            ["BorderHighlightBrush"] = Brush(WithAlpha(accent, 0.58)),
            ["TopHighlightBrush"] = Brush(WithAlpha(primaryText, 0.12)),
            ["BottomEdgeBrush"] = Brush(WithAlpha(Colors.Black, 0.3)),
            ["TextPrimaryBrush"] = Brush(primaryText),
            ["TextSecondaryBrush"] = Brush(secondaryText),
            ["TextTertiaryBrush"] = Brush(WithAlpha(secondaryText, 0.62)),
            ["AccentBrush"] = Brush(accent),
            ["AccentForegroundBrush"] = Brush(ThemeColorContrast.GetContrastingForeground(accent, background)),
            ["AccentHoverBrush"] = Brush(Adjust(accent, 1.12)),
            ["PrimaryButtonBackground"] = Brush(primaryButton),
            ["PrimaryButtonHoverBackground"] = Brush(primaryHover),
            ["PrimaryButtonPressedBackground"] = Brush(primaryPressed),
            ["PrimaryButtonDisabledBackground"] = Brush(primaryDisabled),
            ["SecondaryButtonBackground"] = Brush(secondaryButton),
            ["MenuBorder"] = Brush(border),
            ["HoverBrush"] = Brush(hover),
            ["SelectedBrush"] = Brush(selected),
            ["PressedBrush"] = Brush(WithAlpha(Colors.Black, 0.22)),
            ["FocusBrush"] = Brush(WithAlpha(accent, 0.82)),
            ["DangerBrush"] = Brush(Color.FromRgb(255, 104, 120)),
            ["IconForeground"] = Brush(primaryText),
            ["IconPrimary"] = Brush(primaryText),
            ["IconAccent"] = Brush(accent),
            ["IconMuted"] = Brush(WithAlpha(secondaryText, 0.56)),
            ["ShadowColor"] = Color.FromArgb(190, 0, 0, 0)
        };
        CompleteSemanticContrastContract(dictionary, background, settings.HoverIntensity);
        CompleteBackgroundContract(dictionary, settings);
        return dictionary;
    }

    private static void CompleteSemanticContrastContract(ResourceDictionary dictionary, Color? surfaceBehind = null,
        double hoverIntensity = DefaultHoverIntensity)
    {
        var applicationBackground = surfaceBehind ?? RepresentativeColor(
            dictionary["BackgroundBrush"] as Brush, Colors.Black);

        // Status indicators use theme-independent semantic colors. Keeping them
        // in the theme resource contract lets the Activity view stay free of
        // hardcoded RGB values while remaining recognizable in every theme.
        if (!dictionary.Contains("SuccessBrush")) dictionary["SuccessBrush"] = Brush(Colors.ForestGreen);
        if (!dictionary.Contains("WarningBrush")) dictionary["WarningBrush"] = Brush(Colors.Goldenrod);

        EnsureTextContrast("TextPrimaryBrush", "BackgroundBrush");
        EnsureTextContrast("TextSecondaryBrush", "BackgroundBrush");
        // All four areas are primary panels. Individual panel opacity is materialized
        // for custom themes before this method runs; built-in themes use SurfaceBrush
        // directly so they cannot drift to a separate semantic surface.
        if (!dictionary.Contains("CategoriesSurfaceBrush")) dictionary["CategoriesSurfaceBrush"] = dictionary["SurfaceBrush"];
        if (!dictionary.Contains("ProfilesSurfaceBrush")) dictionary["ProfilesSurfaceBrush"] = dictionary["SurfaceBrush"];
        if (!dictionary.Contains("ProfileEditorSurfaceBrush")) dictionary["ProfileEditorSurfaceBrush"] = dictionary["SurfaceBrush"];
        if (!dictionary.Contains("ActivitySurfaceBrush")) dictionary["ActivitySurfaceBrush"] = dictionary["SurfaceBrush"];
        if (!dictionary.Contains("SettingsRowBackgroundBrush"))
        {
            var background = RepresentativeColor(dictionary["BackgroundBrush"] as Brush, Colors.Black);
            var elevated = RepresentativeColor(dictionary["ElevatedSurfaceBrush"] as Brush, background);
            var panel = RepresentativeColor(dictionary["SurfaceBrush"] as Brush, elevated);
            dictionary["SettingsRowBackgroundBrush"] = Brush(CreateSettingsRowBackground(background, elevated, panel));
        }

        Set("PrimaryButtonBackground", "PrimaryButtonForeground");
        Set("PrimaryButtonHoverBackground", "PrimaryButtonHoverForeground");
        Set("PrimaryButtonPressedBackground", "PrimaryButtonPressedForeground");
        Set("PrimaryButtonDisabledBackground", "PrimaryButtonDisabledForeground");
        Set("HoverBrush", "HoverForeground");
        Set("SelectedBrush", "SelectionForeground");

        // Interactive cards and rows use one shared hover. Keep the source brush
        // (including gradients and hue), but map the normalized editor value to a
        // capped alpha scale so 100% cannot turn into a Selected-like surface.
        var hover = dictionary["HoverBrush"] as Brush ?? Brush(Color.FromArgb(0, 255, 255, 255));
        var scale = MaxInteractiveHoverScale * Math.Clamp(hoverIntensity, 0, 100) / 100d;
        dictionary["InteractiveHoverBrush"] = TransformBrush(hover,
            color => WithAlpha(color, color.A / 255d * scale));

        var secondary = dictionary["ElevatedSurfaceBrush"] as Brush;
        secondary ??= Brush(applicationBackground);
        dictionary["SecondaryButtonBackground"] = secondary;
        dictionary["SecondaryButtonHoverBackground"] = TransformBrush(secondary,
            color => CompositeOverlay(color, RepresentativeColor(dictionary["HoverBrush"] as Brush, Colors.Transparent)));
        dictionary["SecondaryButtonPressedBackground"] = TransformBrush(secondary,
            color => CompositeOverlay(color, RepresentativeColor(dictionary["PressedBrush"] as Brush, Colors.Transparent)));
        dictionary["SecondaryButtonDisabledBackground"] = TransformBrush(secondary,
            color => Blend(color, applicationBackground, 0.58));
        dictionary["SecondaryButtonBorder"] = dictionary["BorderBrush"];
        Set("SecondaryButtonBackground", "SecondaryButtonForeground");
        Set("SecondaryButtonHoverBackground", "SecondaryButtonHoverForeground");
        Set("SecondaryButtonPressedBackground", "SecondaryButtonPressedForeground");
        Set("SecondaryButtonDisabledBackground", "SecondaryButtonDisabledForeground");

        var popup = dictionary["ElevatedSurfaceBrush"] as Brush ?? secondary;
        dictionary["PopupBackgroundBrush"] = OpaqueBrush(popup);
        var menu = dictionary["PopupBackgroundBrush"] as Brush;
        menu ??= secondary;
        dictionary["MenuBackground"] = menu;
        dictionary["MenuHoverBackground"] = TransformBrush(menu,
            color => CompositeOverlay(color, RepresentativeColor(dictionary["HoverBrush"] as Brush, Colors.Transparent)));
        dictionary["MenuPressedBackground"] = TransformBrush(menu,
            color => CompositeOverlay(color, RepresentativeColor(dictionary["PressedBrush"] as Brush, Colors.Transparent)));
        dictionary["MenuDisabledBackground"] = TransformBrush(menu,
            color => Blend(color, applicationBackground, 0.35));
        dictionary["MenuBorder"] = dictionary["BorderHighlightBrush"];
        Set("MenuBackground", "MenuForeground");
        Set("MenuHoverBackground", "MenuHoverForeground");
        Set("MenuPressedBackground", "MenuPressedForeground");
        Set("MenuDisabledBackground", "MenuDisabledForeground");
        var input = dictionary["InputBackgroundBrush"] as Brush ?? secondary;
        dictionary["ScrollBarTrack"] = TransformBrush(input,
            color => Blend(color, applicationBackground, 0.12));
        var scrollThumbSource = dictionary["TextSecondaryBrush"] as Brush ?? Brush(applicationBackground);
        dictionary["ScrollBarThumb"] = TransformBrush(scrollThumbSource, color => WithAlpha(color, 0.38));
        dictionary["ScrollBarThumbHover"] = TransformBrush(scrollThumbSource, color => WithAlpha(color, 0.58));
        dictionary["ScrollBarThumbPressed"] = TransformBrush(scrollThumbSource, color => WithAlpha(color, 0.74));

        dictionary["DisabledInputBackground"] = TransformBrush(input,
            color => Blend(color, applicationBackground, 0.32));
        Set("DisabledInputBackground", "DisabledInputForeground");

        void Set(string backgroundKey, string foregroundKey)
        {
            if (!dictionary.Contains(backgroundKey) || dictionary[backgroundKey] is not Brush background) return;
            dictionary[foregroundKey] = Brush(ThemeColorContrast.GetContrastingForeground(
                GetColors(background), applicationBackground));
        }

        void EnsureTextContrast(string foregroundKey, string backgroundKey)
        {
            if (dictionary[foregroundKey] is not SolidColorBrush foreground ||
                dictionary[backgroundKey] is not Brush background) return;
            var colors = GetColors(background);
            if (ThemeColorContrast.MeetsContrast(foreground.Color, colors, 4.5, applicationBackground)) return;
            dictionary[foregroundKey] = Brush(ThemeColorContrast.GetContrastingForeground(colors, applicationBackground));
        }
    }

    private void CompleteBackgroundContract(ResourceDictionary dictionary, CustomThemeSettings? settings = null)
    {
        var path = settings?.PreviewBackgroundPath;
        if (string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(settings?.BackgroundAssetFileName))
            path = Path.Combine(paths.CustomThemeDirectory, settings.BackgroundAssetFileName);
        if (!File.Exists(path)) path = string.Empty;
        dictionary["CustomBackgroundPath"] = path ?? string.Empty;
        dictionary["CustomBackgroundOpacity"] = settings is null ? 0d : Math.Clamp(settings.BackgroundOpacity, 0, 1);
        dictionary["CustomBackgroundStretch"] = ParseStretch(settings?.ImageFit);
        dictionary["CustomBackgroundOverlayBrush"] = Brush(Color.FromArgb(
            (byte)Math.Round(Math.Clamp(settings?.DarkOverlay ?? 0, 0, 1) * 255), 0, 0, 0));
    }

    private bool IsThemeDictionary(ResourceDictionary dictionary)
    {
        if (dictionary.Contains(ThemeMarker)) return true;
        return dictionary.Source is not null && _availableThemes.Any(theme =>
            dictionary.Source.OriginalString.EndsWith(GetResourceFileName(theme.ResourceUri), StringComparison.OrdinalIgnoreCase));
    }

    private static Stretch ParseStretch(string? value) => value?.ToLowerInvariant() switch
    {
        "fill" => Stretch.Fill,
        "uniform" => Stretch.Uniform,
        "stretch" => Stretch.Fill,
        _ => Stretch.UniformToFill
    };

    private static Color Parse(string? value, string fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(value ?? fallback); }
        catch (FormatException) { return (Color)ColorConverter.ConvertFromString(fallback); }
    }

    private static Color EnsureReadable(Color requested, Color background)
    {
        var opaque = Color.FromRgb(requested.R, requested.G, requested.B);
        return ThemeColorContrast.GetContrastRatio(opaque, background) >= 4.5
            ? opaque
            : ThemeColorContrast.GetContrastingForeground(background);
    }

    private static IReadOnlyList<Color> GetColors(Brush brush) => brush switch
    {
        SolidColorBrush solid => [solid.Color],
        GradientBrush gradient when gradient.GradientStops.Count > 0 => gradient.GradientStops.Select(stop => stop.Color).ToArray(),
        _ => [Colors.Black]
    };

    private static Color RepresentativeColor(Brush? brush, Color fallback)
    {
        if (brush is null) return fallback;
        var colors = GetColors(brush);
        return Color.FromArgb(
            (byte)Math.Round(colors.Average(color => color.A)),
            (byte)Math.Round(colors.Average(color => color.R)),
            (byte)Math.Round(colors.Average(color => color.G)),
            (byte)Math.Round(colors.Average(color => color.B)));
    }

    private static Brush TransformBrush(Brush source, Func<Color, Color> transform)
    {
        Brush result = source switch
        {
            SolidColorBrush solid => new SolidColorBrush(transform(solid.Color)),
            LinearGradientBrush linear => TransformGradient(linear, transform),
            RadialGradientBrush radial => TransformGradient(radial, transform),
            _ => new SolidColorBrush(transform(RepresentativeColor(source, Colors.Black)))
        };
        if (result.CanFreeze) result.Freeze();
        return result;
    }

    private static Brush OpaqueBrush(Brush source) => TransformBrush(source, color => WithAlpha(color, 1));

    private static T TransformGradient<T>(T source, Func<Color, Color> transform) where T : GradientBrush
    {
        var result = (T)source.CloneCurrentValue();
        foreach (var stop in result.GradientStops) stop.Color = transform(stop.Color);
        return result;
    }

    private static Color CompositeOverlay(Color background, Color overlay)
    {
        var alpha = overlay.A / 255d;
        return Color.FromArgb(255,
            (byte)Math.Round(overlay.R * alpha + background.R * (1 - alpha)),
            (byte)Math.Round(overlay.G * alpha + background.G * (1 - alpha)),
            (byte)Math.Round(overlay.B * alpha + background.B * (1 - alpha)));
    }

    private static SolidColorBrush Brush(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
    private static Color WithAlpha(Color color, double alpha) => Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), color.R, color.G, color.B);
    private static Color ApplySurfaceOpacity(Color color, double opacity) => Color.FromArgb(
        (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255), color.R, color.G, color.B);
    private static Color Blend(Color value, Color other, double otherAmount) => Color.FromArgb(255,
        (byte)(value.R * (1 - otherAmount) + other.R * otherAmount),
        (byte)(value.G * (1 - otherAmount) + other.G * otherAmount),
        (byte)(value.B * (1 - otherAmount) + other.B * otherAmount));
    private static Color Adjust(Color value, double factor) => Color.FromArgb(value.A,
        (byte)Math.Clamp(value.R * factor, 0, 255), (byte)Math.Clamp(value.G * factor, 0, 255),
        (byte)Math.Clamp(value.B * factor, 0, 255));

    private static Color CreateSettingsRowBackground(Color background, Color elevated, Color panel)
    {
        var row = Blend(elevated, background, 0.18);
        var lightBackground = RelativeLuminance(background) > 0.5;
        if (ColorDistance(row, background) < 18)
            row = lightBackground ? Adjust(background, 0.92) : Adjust(background, 1.18);
        if (ColorDistance(row, panel) < 10)
            row = lightBackground ? Adjust(row, 0.95) : Adjust(row, 1.08);
        return Color.FromRgb(row.R, row.G, row.B);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Channel(byte value)
        {
            var normalized = value / 255d;
            return normalized <= 0.03928 ? normalized / 12.92 : Math.Pow((normalized + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    private static double ColorDistance(Color first, Color second) =>
        Math.Sqrt(Math.Pow(first.R - second.R, 2) + Math.Pow(first.G - second.G, 2) + Math.Pow(first.B - second.B, 2));
    private static string GetResourceFileName(Uri resourceUri) => resourceUri.OriginalString[(resourceUri.OriginalString.LastIndexOf('/') + 1)..];
    private static ThemeDefinition Create(string id, string displayName, string fileName) => new(id, displayName,
        new Uri($"/SwitchBoard;component/Themes/{fileName}", UriKind.Relative));
}
