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
    public string Selected { get; set; } = "#5572A7FF";
    public string PrimaryButton { get; set; } = "#FF72A7FF";
    public string IconAccent { get; set; } = "#FFA9C9FF";
    public string? BackgroundAssetFileName { get; set; }
    public string ImageFit { get; set; } = "uniformToFill";
    public double BackgroundOpacity { get; set; } = 0.42;
    public double DarkOverlay { get; set; } = 0.38;

    [JsonIgnore]
    public string? PreviewBackgroundPath { get; set; }

    public static CustomThemeSettings CreateDefault() => new();

    public CustomThemeSettings Clone() => new()
    {
        Background = Background, Panel = Panel, Card = Card, Elevated = Elevated, Border = Border,
        PrimaryText = PrimaryText, SecondaryText = SecondaryText, Accent = Accent, Hover = Hover,
        Selected = Selected, PrimaryButton = PrimaryButton, IconAccent = IconAccent,
        BackgroundAssetFileName = BackgroundAssetFileName, ImageFit = ImageFit,
        BackgroundOpacity = BackgroundOpacity, DarkOverlay = DarkOverlay,
        PreviewBackgroundPath = PreviewBackgroundPath
    };
}
