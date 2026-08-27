using System.Text.Json.Serialization;

namespace SwitchBoard.Themes;

public sealed class CustomThemeSettings
{
    public string Background { get; set; } = "#FF11141B";
    public string Panel { get; set; } = "#F01A1F29";
    public string Card { get; set; } = "#F0232935";
    public string Elevated { get; set; } = "#FF2B3240";
    public string Border { get; set; } = "#52FFFFFF";
    public string PrimaryText { get; set; } = "#FFF5F7FA";
    public string SecondaryText { get; set; } = "#FFB5BDCA";
    public string Accent { get; set; } = "#FF72A7FF";
    public string Hover { get; set; } = "#3372A7FF";
    public string Selection { get; set; } = "#5572A7FF";
    public string PrimaryButtonBackground { get; set; } = "#FF72A7FF";
    public string PrimaryButtonForeground { get; set; } = "auto";
    // Retained for backward-compatible deserialization. ThemeManager derives these
    // control brushes from the shared semantic surfaces for every newly applied theme.
    public string SecondaryButtonBackground { get; set; } = "auto";
    public string SecondaryButtonForeground { get; set; } = "auto";
    public string IconForeground { get; set; } = "#FFA9C9FF";
    public string MenuBackground { get; set; } = "auto";
    public string MenuForeground { get; set; } = "auto";
    public string MenuHoverBackground { get; set; } = "auto";
    public double SurfaceOpacity { get; set; } = 1.0;
    // The slider is normalized to 0-100. ThemeManager maps it to a capped
    // alpha scale so the strongest hover remains weaker than selection.
    public double HoverIntensity { get; set; } = 78.0;
    public double CategoriesPanelOpacity { get; set; } = 1.0;
    public double ProfilesPanelOpacity { get; set; } = 1.0;
    public double ProfileEditorPanelOpacity { get; set; } = 1.0;
    public double ActivityPanelOpacity { get; set; } = 1.0;
    public string? BackgroundAssetFileName { get; set; }
    public string ImageFit { get; set; } = "uniformToFill";
    public double BackgroundOpacity { get; set; } = 0.42;
    public double DarkOverlay { get; set; } = 0.38;

    // Schema 5 aliases. Cleared during startup normalization.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Selected { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrimaryButton { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IconAccent { get; set; }

    [JsonIgnore]
    public string? PreviewBackgroundPath { get; set; }

    public static CustomThemeSettings CreateDefault() => new();

    public void NormalizeLegacy()
    {
        if (!string.IsNullOrWhiteSpace(Selected)) Selection = Selected;
        if (!string.IsNullOrWhiteSpace(PrimaryButton)) PrimaryButtonBackground = PrimaryButton;
        if (!string.IsNullOrWhiteSpace(IconAccent)) IconForeground = IconAccent;
        Selected = null;
        PrimaryButton = null;
        IconAccent = null;
        if (string.IsNullOrWhiteSpace(PrimaryButtonForeground)) PrimaryButtonForeground = "auto";
        if (string.IsNullOrWhiteSpace(SecondaryButtonBackground)) SecondaryButtonBackground = "auto";
        if (string.IsNullOrWhiteSpace(SecondaryButtonForeground)) SecondaryButtonForeground = "auto";
        if (string.IsNullOrWhiteSpace(MenuBackground)) MenuBackground = "auto";
        if (string.IsNullOrWhiteSpace(MenuForeground)) MenuForeground = "auto";
        if (string.IsNullOrWhiteSpace(MenuHoverBackground)) MenuHoverBackground = "auto";
        SurfaceOpacity = Math.Clamp(SurfaceOpacity, 0, 1);
        HoverIntensity = Math.Clamp(HoverIntensity, 0, 100);
        CategoriesPanelOpacity = Math.Clamp(CategoriesPanelOpacity, 0, 1);
        ProfilesPanelOpacity = Math.Clamp(ProfilesPanelOpacity, 0, 1);
        ProfileEditorPanelOpacity = Math.Clamp(ProfileEditorPanelOpacity, 0, 1);
        ActivityPanelOpacity = Math.Clamp(ActivityPanelOpacity, 0, 1);
    }

    public void MigrateSurfaceOpacityFromLegacyAlpha()
    {
        if (string.IsNullOrWhiteSpace(Panel) || Panel.Length != 9 || Panel[0] != '#') return;
        if (byte.TryParse(Panel.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var alpha))
        {
            SurfaceOpacity = alpha / 255d;
            CategoriesPanelOpacity = SurfaceOpacity;
            ProfilesPanelOpacity = SurfaceOpacity;
            ProfileEditorPanelOpacity = SurfaceOpacity;
            ActivityPanelOpacity = SurfaceOpacity;
        }
    }

    public void MigrateActivityOpacity() => ActivityPanelOpacity = SurfaceOpacity;

    public CustomThemeSettings Clone()
    {
        var result = new CustomThemeSettings
        {
            Background = Background, Panel = Panel, Card = Card, Elevated = Elevated, Border = Border,
            PrimaryText = PrimaryText, SecondaryText = SecondaryText, Accent = Accent, Hover = Hover,
            Selection = Selected ?? Selection,
            PrimaryButtonBackground = PrimaryButton ?? PrimaryButtonBackground,
            PrimaryButtonForeground = PrimaryButtonForeground,
            SecondaryButtonBackground = SecondaryButtonBackground,
            SecondaryButtonForeground = SecondaryButtonForeground,
            IconForeground = IconAccent ?? IconForeground,
            MenuBackground = MenuBackground,
            MenuForeground = MenuForeground,
            MenuHoverBackground = MenuHoverBackground,
            SurfaceOpacity = SurfaceOpacity,
            HoverIntensity = HoverIntensity,
            CategoriesPanelOpacity = CategoriesPanelOpacity,
            ProfilesPanelOpacity = ProfilesPanelOpacity,
            ProfileEditorPanelOpacity = ProfileEditorPanelOpacity,
            ActivityPanelOpacity = ActivityPanelOpacity,
            BackgroundAssetFileName = BackgroundAssetFileName, ImageFit = ImageFit,
            BackgroundOpacity = BackgroundOpacity, DarkOverlay = DarkOverlay,
            PreviewBackgroundPath = PreviewBackgroundPath
        };
        result.NormalizeLegacy();
        return result;
    }
}
