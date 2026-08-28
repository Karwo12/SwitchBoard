namespace SwitchBoard.ViewModels;

/// <summary>Presentation-only option for profile color and icon selectors.</summary>
public sealed record ProfileAppearanceOption(string? Value, string DisplayName, string? Swatch = null);
