using System.Collections.ObjectModel;
using SwitchBoard.Localization;
using SwitchBoard.Services;
using SwitchBoard.Themes;

namespace SwitchBoard.ViewModels;

public sealed class CustomThemeEditorViewModel : ObservableObject
{
    private readonly ILocalizationService _localization;
    private readonly CustomThemeSettings _resetTemplate;
    private readonly IReadOnlyCollection<string> _unavailableNames;
    private readonly Action<CustomThemeSettings>? _applyTemporary;
    private CustomThemeSettings _settings;
    private string _name;
    private string _warning = string.Empty;

    public CustomThemeEditorViewModel(CustomThemeEditRequest request, ILocalizationService localization)
    {
        _localization = localization;
        Mode = request.Mode;
        _name = request.Name;
        _settings = request.Colors.Clone();
        _settings.NormalizeLegacy();
        MaterializeAutomaticBackgrounds(_settings);
        _resetTemplate = request.Colors.Clone();
        MaterializeAutomaticBackgrounds(_resetTemplate);
        _unavailableNames = request.UnavailableNames;
        _applyTemporary = request.ApplyTemporary;
        Colors = [];
        BuildColors();
        ImageFitOptions =
        [
            new("fill", localization.GetString("CustomTheme.Fit.Fill")),
            new("uniform", localization.GetString("CustomTheme.Fit.Uniform")),
            new("uniformToFill", localization.GetString("CustomTheme.Fit.UniformToFill")),
            new("stretch", localization.GetString("CustomTheme.Fit.Stretch"))
        ];
    }

    public CustomThemeEditMode Mode { get; }
    public ObservableCollection<CustomThemeColorItemViewModel> Colors { get; }
    public IReadOnlyList<CustomThemeFitOption> ImageFitOptions { get; }
    public CustomThemeSettings Settings => _settings;
    public string WindowTitle => Mode == CustomThemeEditMode.Add
        ? _localization.GetString("CustomTheme.Add")
        : _localization.Format("CustomTheme.EditTitle", Name);
    public string PrimaryActionText => Mode switch
    {
        CustomThemeEditMode.Add => _localization.GetString("CustomTheme.Add"),
        CustomThemeEditMode.EditCustom => _localization.GetString("CustomTheme.SaveChanges"),
        _ => _localization.GetString("CustomTheme.SaveAsNew")
    };
    public string Name
    {
        get => _name;
        set
        {
            if (!SetProperty(ref _name, value)) return;
            OnPropertyChanged(nameof(WindowTitle));
            OnPropertyChanged(nameof(NameError));
            OnPropertyChanged(nameof(IsNameValid));
        }
    }
    public bool IsNameValid => string.IsNullOrEmpty(NameError);
    public string NameError
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Name)) return _localization.GetString("CustomTheme.NameRequired");
            return _unavailableNames.Any(item => string.Equals(item.Trim(), Name.Trim(), StringComparison.CurrentCultureIgnoreCase))
                ? _localization.GetString("CustomTheme.DuplicateName") : string.Empty;
        }
    }
    public string BackgroundFileName => _settings.BackgroundAssetFileName ?? string.Empty;
    public bool HasBackground => !string.IsNullOrWhiteSpace(_settings.BackgroundAssetFileName);
    public double SurfaceOpacityPercent
    {
        get => _settings.SurfaceOpacity * 100;
        set
        {
            var opacity = Math.Clamp(value / 100, 0, 1);
            _settings.SurfaceOpacity = opacity;
            _settings.CategoriesPanelOpacity = opacity;
            _settings.ProfilesPanelOpacity = opacity;
            _settings.ProfileEditorPanelOpacity = opacity;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CategoriesPanelOpacityPercent));
            OnPropertyChanged(nameof(ProfilesPanelOpacityPercent));
            OnPropertyChanged(nameof(ProfileEditorPanelOpacityPercent));
            ApplyDraft();
        }
    }
    public double CategoriesPanelOpacityPercent
    {
        get => _settings.CategoriesPanelOpacity * 100;
        set { _settings.CategoriesPanelOpacity = Math.Clamp(value / 100, 0, 1); OnPropertyChanged(); ApplyDraft(); }
    }
    public double ProfilesPanelOpacityPercent
    {
        get => _settings.ProfilesPanelOpacity * 100;
        set { _settings.ProfilesPanelOpacity = Math.Clamp(value / 100, 0, 1); OnPropertyChanged(); ApplyDraft(); }
    }
    public double ProfileEditorPanelOpacityPercent
    {
        get => _settings.ProfileEditorPanelOpacity * 100;
        set { _settings.ProfileEditorPanelOpacity = Math.Clamp(value / 100, 0, 1); OnPropertyChanged(); ApplyDraft(); }
    }
    public double BackgroundOpacityPercent
    {
        get => _settings.BackgroundOpacity * 100;
        set { _settings.BackgroundOpacity = Math.Clamp(value / 100, 0, 1); OnPropertyChanged(); ApplyDraft(); }
    }
    public double DarkOverlayPercent
    {
        get => _settings.DarkOverlay * 100;
        set { _settings.DarkOverlay = Math.Clamp(value / 100, 0, 1); OnPropertyChanged(); ApplyDraft(); }
    }
    public string ImageFit
    {
        get => _settings.ImageFit;
        set { if (_settings.ImageFit == value) return; _settings.ImageFit = value; OnPropertyChanged(); ApplyDraft(); }
    }
    public string Warning { get => _warning; set => SetProperty(ref _warning, value); }

    public void SetBackground(string? assetFileName, string? previewPath)
    {
        _settings.BackgroundAssetFileName = assetFileName;
        _settings.PreviewBackgroundPath = previewPath;
        OnPropertyChanged(nameof(BackgroundFileName));
        OnPropertyChanged(nameof(HasBackground));
        ApplyDraft();
    }

    public void Reset()
    {
        var preservedName = Name;
        _settings = _resetTemplate.Clone();
        Name = preservedName;
        Colors.Clear();
        BuildColors();
        OnPropertyChanged(nameof(BackgroundFileName));
        OnPropertyChanged(nameof(HasBackground));
        OnPropertyChanged(nameof(SurfaceOpacityPercent));
        OnPropertyChanged(nameof(CategoriesPanelOpacityPercent));
        OnPropertyChanged(nameof(ProfilesPanelOpacityPercent));
        OnPropertyChanged(nameof(ProfileEditorPanelOpacityPercent));
        OnPropertyChanged(nameof(BackgroundOpacityPercent));
        OnPropertyChanged(nameof(DarkOverlayPercent));
        OnPropertyChanged(nameof(ImageFit));
        Warning = string.Empty;
        ApplyDraft();
    }

    private void BuildColors()
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
        Add("selection", "CustomTheme.Color.Selected", () => _settings.Selection, value => _settings.Selection = value);
        Add("primaryButtonBackground", "CustomTheme.Color.PrimaryButton", () => _settings.PrimaryButtonBackground, value => _settings.PrimaryButtonBackground = value);
        Add("secondaryButtonBackground", "CustomTheme.Color.SecondaryButton", () => _settings.SecondaryButtonBackground, value => _settings.SecondaryButtonBackground = value);
        Add("iconForeground", "CustomTheme.Color.IconAccent", () => _settings.IconForeground, value => _settings.IconForeground = value);
        Add("menuBackground", "CustomTheme.Color.MenuBackground", () => _settings.MenuBackground, value => _settings.MenuBackground = value);
        Add("menuHoverBackground", "CustomTheme.Color.MenuHover", () => _settings.MenuHoverBackground, value => _settings.MenuHoverBackground = value);
    }

    private void Add(string key, string resource, Func<string> read, Action<string> write) => Colors.Add(
        new CustomThemeColorItemViewModel(key, _localization.GetString(resource), read, write, ApplyDraft));

    private void ApplyDraft()
    {
        _settings.PrimaryButtonForeground = "auto";
        _settings.SecondaryButtonForeground = "auto";
        _settings.MenuForeground = "auto";
        _applyTemporary?.Invoke(_settings.Clone());
    }

    private static void MaterializeAutomaticBackgrounds(CustomThemeSettings settings)
    {
        if (IsAuto(settings.SecondaryButtonBackground)) settings.SecondaryButtonBackground = settings.Elevated;
        if (IsAuto(settings.MenuBackground)) settings.MenuBackground = settings.Panel;
        if (IsAuto(settings.MenuHoverBackground)) settings.MenuHoverBackground = settings.Hover;
    }

    private static bool IsAuto(string? value) => string.IsNullOrWhiteSpace(value) ||
        string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase);
}

public sealed record CustomThemeFitOption(string Value, string DisplayName);
