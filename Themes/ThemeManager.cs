using System.IO;
using System.Windows;
using System.Windows.Media;
using SwitchBoard.Data;

namespace SwitchBoard.Themes;

public sealed class ThemeManager(AppDataPaths paths) : IThemeManager
{
    private const string ThemeMarker = "SwitchBoard.Theme.Dictionary";
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
            Selection = Read("SelectedBrush", "#5572A7FF"),
            PrimaryButtonBackground = Read("PrimaryButtonBackground", "#FF72A7FF"),
            PrimaryButtonForeground = "auto",
            SecondaryButtonBackground = Read("ElevatedSurfaceBrush", "#FF2B3240"),
            SecondaryButtonForeground = "auto",
            IconForeground = Read("IconForeground", Read("IconAccent", "#FFA9C9FF")),
            MenuBackground = Read("PopupBackgroundBrush", "#FF1A1F29"),
            MenuForeground = "auto",
            MenuHoverBackground = Read("SelectedBrush", "#FF303746"),
            SurfaceOpacity = RepresentativeColor(dictionary["SurfaceBrush"] as Brush, Colors.White).A / 255d,
            CategoriesPanelOpacity = RepresentativeColor(dictionary["SurfaceBrush"] as Brush, Colors.White).A / 255d,
            ProfilesPanelOpacity = RepresentativeColor(dictionary["SurfaceBrush"] as Brush, Colors.White).A / 255d,
            ProfileEditorPanelOpacity = RepresentativeColor(dictionary["SurfaceBrush"] as Brush, Colors.White).A / 255d,
            ActivityPanelOpacity = RepresentativeColor(dictionary["ElevatedSurfaceBrush"] as Brush, Colors.White).A / 255d
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
        var activityPanel = ApplySurfaceOpacity(elevatedBase, settings.ActivityPanelOpacity);
        var border = Parse(settings.Border, "#52FFFFFF");
        var primaryText = EnsureReadable(Parse(settings.PrimaryText, "#FFF5F7FA"), background);
        var secondaryText = EnsureReadable(Parse(settings.SecondaryText, "#FFB5BDCA"), background);
        var accent = Parse(settings.Accent, "#FF72A7FF");
        var hover = Parse(settings.Hover, "#3372A7FF");
        var selected = Parse(settings.Selection, "#5572A7FF");
        var primaryButton = Parse(settings.PrimaryButtonBackground, "#FF72A7FF");
        var secondaryButton = ParseAuto(settings.SecondaryButtonBackground, elevatedBase);
        var iconForeground = Parse(settings.IconForeground, "#FFA9C9FF");
        var menuBackgroundBase = ParseAuto(settings.MenuBackground, panelBase);
        var menuHoverBackgroundBase = ParseAuto(settings.MenuHoverBackground, Adjust(menuBackgroundBase, 1.12));
        // Popup/menu surfaces must be opaque so glass panels behind them cannot bleed through.
        var menuBackground = WithAlpha(menuBackgroundBase, 1);
        var menuHoverBackground = WithAlpha(menuHoverBackgroundBase, 1);
        var primaryHover = Adjust(primaryButton, 1.12);
        var primaryPressed = Adjust(primaryButton, 0.82);
        var primaryDisabled = Blend(primaryButton, background, 0.65);
        var secondaryHover = Adjust(secondaryButton, 1.10);
        var secondaryPressed = Adjust(secondaryButton, 0.84);
        var secondaryDisabled = Blend(secondaryButton, background, 0.58);
        var menuPressed = Adjust(menuHoverBackground, 0.86);
        var menuDisabledBackground = Blend(menuBackground, background, 0.35);
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
            ["PopupBackgroundBrush"] = Brush(WithAlpha(panel, 1)),
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
            ["SecondaryButtonHoverBackground"] = Brush(secondaryHover),
            ["SecondaryButtonPressedBackground"] = Brush(secondaryPressed),
            ["SecondaryButtonDisabledBackground"] = Brush(secondaryDisabled),
            ["SecondaryButtonBorder"] = Brush(border),
            ["MenuBackground"] = Brush(menuBackground),
            ["MenuHoverBackground"] = Brush(menuHoverBackground),
            ["MenuPressedBackground"] = Brush(menuPressed),
            ["MenuDisabledBackground"] = Brush(menuDisabledBackground),
            ["MenuBorder"] = Brush(border),
            ["HoverBrush"] = Brush(hover),
            ["SelectedBrush"] = Brush(selected),
            ["PressedBrush"] = Brush(WithAlpha(Colors.Black, 0.22)),
            ["FocusBrush"] = Brush(WithAlpha(accent, 0.82)),
            ["DangerBrush"] = Brush(Color.FromRgb(255, 104, 120)),
            ["IconForeground"] = Brush(iconForeground),
            ["IconPrimary"] = Brush(iconForeground),
            ["IconAccent"] = Brush(iconForeground),
            ["IconMuted"] = Brush(WithAlpha(secondaryText, 0.56)),
            ["ShadowColor"] = Color.FromArgb(190, 0, 0, 0)
        };
        CompleteSemanticContrastContract(dictionary, background);
        CompleteBackgroundContract(dictionary, settings);
        return dictionary;
    }

    private static void CompleteSemanticContrastContract(ResourceDictionary dictionary, Color? surfaceBehind = null)
    {
        var applicationBackground = surfaceBehind ?? RepresentativeColor(
            dictionary["BackgroundBrush"] as Brush, Colors.Black);

        EnsureTextContrast("TextPrimaryBrush", "BackgroundBrush");
        EnsureTextContrast("TextSecondaryBrush", "BackgroundBrush");
        if (!dictionary.Contains("CategoriesSurfaceBrush")) dictionary["CategoriesSurfaceBrush"] = dictionary["SurfaceBrush"];
        if (!dictionary.Contains("ProfilesSurfaceBrush")) dictionary["ProfilesSurfaceBrush"] = dictionary["SurfaceBrush"];
        if (!dictionary.Contains("ProfileEditorSurfaceBrush")) dictionary["ProfileEditorSurfaceBrush"] = dictionary["SurfaceBrush"];
        if (!dictionary.Contains("ActivitySurfaceBrush")) dictionary["ActivitySurfaceBrush"] = dictionary["ElevatedSurfaceBrush"];

        Set("PrimaryButtonBackground", "PrimaryButtonForeground");
        Set("PrimaryButtonHoverBackground", "PrimaryButtonHoverForeground");
        Set("PrimaryButtonPressedBackground", "PrimaryButtonPressedForeground");
        Set("PrimaryButtonDisabledBackground", "PrimaryButtonDisabledForeground");
        Set("HoverBrush", "HoverForeground");
        Set("SelectedBrush", "SelectionForeground");

        var secondary = dictionary.Contains("SecondaryButtonBackground")
            ? dictionary["SecondaryButtonBackground"] as Brush
            : dictionary["ElevatedSurfaceBrush"] as Brush;
        secondary ??= Brush(applicationBackground);
        if (!dictionary.Contains("SecondaryButtonBackground")) dictionary["SecondaryButtonBackground"] = secondary;
        if (!dictionary.Contains("SecondaryButtonHoverBackground")) dictionary["SecondaryButtonHoverBackground"] = TransformBrush(secondary,
            color => CompositeOverlay(color, RepresentativeColor(dictionary["HoverBrush"] as Brush, Colors.Transparent)));
        if (!dictionary.Contains("SecondaryButtonPressedBackground")) dictionary["SecondaryButtonPressedBackground"] = TransformBrush(secondary,
            color => CompositeOverlay(color, RepresentativeColor(dictionary["PressedBrush"] as Brush, Colors.Transparent)));
        if (!dictionary.Contains("SecondaryButtonDisabledBackground")) dictionary["SecondaryButtonDisabledBackground"] = TransformBrush(secondary,
            color => Blend(color, applicationBackground, 0.58));
        if (!dictionary.Contains("SecondaryButtonBorder")) dictionary["SecondaryButtonBorder"] = dictionary["BorderBrush"];
        Set("SecondaryButtonBackground", "SecondaryButtonForeground");
        Set("SecondaryButtonHoverBackground", "SecondaryButtonHoverForeground");
        Set("SecondaryButtonPressedBackground", "SecondaryButtonPressedForeground");
        Set("SecondaryButtonDisabledBackground", "SecondaryButtonDisabledForeground");

        var menu = dictionary.Contains("MenuBackground")
            ? dictionary["MenuBackground"] as Brush
            : dictionary["PopupBackgroundBrush"] as Brush;
        menu ??= secondary;
        if (!dictionary.Contains("MenuBackground")) dictionary["MenuBackground"] = menu;
        if (!dictionary.Contains("MenuHoverBackground")) dictionary["MenuHoverBackground"] = TransformBrush(menu,
            color => CompositeOverlay(color, RepresentativeColor(dictionary["HoverBrush"] as Brush, Colors.Transparent)));
        if (!dictionary.Contains("MenuPressedBackground")) dictionary["MenuPressedBackground"] = TransformBrush(menu,
            color => CompositeOverlay(color, RepresentativeColor(dictionary["PressedBrush"] as Brush, Colors.Transparent)));
        if (!dictionary.Contains("MenuDisabledBackground")) dictionary["MenuDisabledBackground"] = TransformBrush(menu,
            color => Blend(color, applicationBackground, 0.35));
        if (!dictionary.Contains("MenuBorder")) dictionary["MenuBorder"] = dictionary["BorderHighlightBrush"];
        Set("MenuBackground", "MenuForeground");
        Set("MenuHoverBackground", "MenuHoverForeground");
        Set("MenuPressedBackground", "MenuPressedForeground");
        Set("MenuDisabledBackground", "MenuDisabledForeground");
        dictionary["ScrollBarTrack"] = dictionary["MenuBackground"];
        dictionary["ScrollBarThumb"] = dictionary["MenuForeground"];
        dictionary["ScrollBarThumbHover"] = dictionary["MenuForeground"];

        var input = dictionary["InputBackgroundBrush"] as Brush ?? secondary;
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

    private static Color ParseAuto(string? value, Color automatic) =>
        string.IsNullOrWhiteSpace(value) || string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase)
            ? automatic
            : Parse(value, automatic.ToString());

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
    private static string GetResourceFileName(Uri resourceUri) => resourceUri.OriginalString[(resourceUri.OriginalString.LastIndexOf('/') + 1)..];
    private static ThemeDefinition Create(string id, string displayName, string fileName) => new(id, displayName,
        new Uri($"/SwitchBoard;component/Themes/{fileName}", UriKind.Relative));
}
