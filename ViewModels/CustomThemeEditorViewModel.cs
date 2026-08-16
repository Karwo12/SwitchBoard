using System.Collections.ObjectModel;
using SwitchBoard.Localization;
using SwitchBoard.Themes;

namespace SwitchBoard.ViewModels;

public sealed class CustomThemeEditorViewModel : ObservableObject
{
    private readonly Action<CustomThemeSettings> _preview;
    private CustomThemeSettings _settings;
    private string _warning = string.Empty;

    public CustomThemeEditorViewModel(CustomThemeSettings settings, ILocalizationService localization,
        Action<CustomThemeSettings> preview)
    {
        _settings = settings;
        _preview = preview;
        Colors = [];
        BuildColors(localization);
        ImageFitOptions =
        [
            new("fill", localization.GetString("CustomTheme.Fit.Fill")),
            new("uniform", localization.GetString("CustomTheme.Fit.Uniform")),
            new("uniformToFill", localization.GetString("CustomTheme.Fit.UniformToFill")),
            new("stretch", localization.GetString("CustomTheme.Fit.Stretch"))
        ];
    }

    public ObservableCollection<CustomThemeColorItemViewModel> Colors { get; }
    public IReadOnlyList<CustomThemeFitOption> ImageFitOptions { get; }
    public CustomThemeSettings Settings => _settings;
    public string BackgroundFileName => _settings.BackgroundAssetFileName ?? string.Empty;
    public bool HasBackground => !string.IsNullOrWhiteSpace(_settings.BackgroundAssetFileName);
    public double BackgroundOpacityPercent
    {
        get => _settings.BackgroundOpacity * 100;
        set { _settings.BackgroundOpacity = Math.Clamp(value / 100, 0, 1); OnPropertyChanged(); _preview(_settings); }
    }
    public double DarkOverlayPercent
    {
        get => _settings.DarkOverlay * 100;
        set { _settings.DarkOverlay = Math.Clamp(value / 100, 0, 1); OnPropertyChanged(); _preview(_settings); }
    }
    public string ImageFit
    {
        get => _settings.ImageFit;
        set { if (_settings.ImageFit == value) return; _settings.ImageFit = value; OnPropertyChanged(); _preview(_settings); }
    }
    public string Warning { get => _warning; set => SetProperty(ref _warning, value); }

    public void SetBackground(string? assetFileName, string? previewPath)
    {
        _settings.BackgroundAssetFileName = assetFileName;
        _settings.PreviewBackgroundPath = previewPath;
        OnPropertyChanged(nameof(BackgroundFileName));
        OnPropertyChanged(nameof(HasBackground));
        _preview(_settings);
    }

    public void Reset(ILocalizationService localization)
    {
        _settings = CustomThemeSettings.CreateDefault();
        Colors.Clear();
        BuildColors(localization);
        OnPropertyChanged(nameof(BackgroundFileName));
        OnPropertyChanged(nameof(HasBackground));
        OnPropertyChanged(nameof(BackgroundOpacityPercent));
        OnPropertyChanged(nameof(DarkOverlayPercent));
        OnPropertyChanged(nameof(ImageFit));
        Warning = string.Empty;
        _preview(_settings);
    }

    private void BuildColors(ILocalizationService localization)
    {
        Add("background", "CustomTheme.Color.Background", () => _settings.Background, value => _settings.Background = value);
        Add("panel", "CustomTheme.Color.Panel", () => _settings.Panel, value => _settings.Panel = value);
        Add("card", "CustomTheme.Color.Card", () => _settings.Card, value => _settings.Card = value);
        Add("elevated", "CustomTheme.Color.Elevated", () => _settings.Elevated, value => _settings.Elevated = value);
        Add("border", "CustomTheme.Color.Border", () => _settings.Border, value => _settings.Border = value);
        Add("primaryText", "CustomTheme.Color.PrimaryText", () => _settings.PrimaryText, value => _settings.PrimaryText = value);
        Add("secondaryText", "CustomTheme.Color.SecondaryText", () => _settings.SecondaryText, value => _settings.SecondaryText = value);
        Add("accent", "CustomTheme.Color.Accent", () => _settings.Accent, value => _settings.Accent = value);
        Add("hover", "CustomTheme.Color.Hover", () => _settings.Hover, value => _settings.Hover = value);
        Add("selected", "CustomTheme.Color.Selected", () => _settings.Selected, value => _settings.Selected = value);
        Add("primaryButton", "CustomTheme.Color.PrimaryButton", () => _settings.PrimaryButton, value => _settings.PrimaryButton = value);
        Add("iconAccent", "CustomTheme.Color.IconAccent", () => _settings.IconAccent, value => _settings.IconAccent = value);

        void Add(string key, string resource, Func<string> read, Action<string> write) => Colors.Add(
            new CustomThemeColorItemViewModel(key, localization.GetString(resource), read, write, () => _preview(_settings)));
    }
}

public sealed record CustomThemeFitOption(string Value, string DisplayName);
