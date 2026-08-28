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
        _resetTemplate = request.Colors.Clone();
        _unavailableNames = request.UnavailableNames;
        _applyTemporary = request.ApplyTemporary;
        Colors = [];
        BuildColors();
        ImageFitOptions =
        [
            new(BackgroundImageFits.Fill, localization.GetString("CustomTheme.Fit.UniformToFill")),
            new(BackgroundImageFits.Fit, localization.GetString("CustomTheme.Fit.Uniform")),
            new(BackgroundImageFits.Stretch, localization.GetString("CustomTheme.Fit.Stretch")),
            new(BackgroundImageFits.Center, localization.GetString("CustomTheme.Fit.Center"))
        ];
        GifAnimationDirectionOptions =
        [
            new(GifAnimationDirections.Normal, localization.GetString("CustomTheme.GifDirection.Normal")),
            new(GifAnimationDirections.Reverse, localization.GetString("CustomTheme.GifDirection.Reverse")),
            new(GifAnimationDirections.PingPong, localization.GetString("CustomTheme.GifDirection.PingPong"))
        ];
        GifAnimationSpeedOptions =
        [
            new(0.5d, localization.GetString("CustomTheme.GifSpeed.Half")),
            new(0.75d, localization.GetString("CustomTheme.GifSpeed.ThreeQuarters")),
            new(1d, localization.GetString("CustomTheme.GifSpeed.One")),
            new(1.25d, localization.GetString("CustomTheme.GifSpeed.OneQuarter")),
            new(1.5d, localization.GetString("CustomTheme.GifSpeed.OneHalf")),
            new(2d, localization.GetString("CustomTheme.GifSpeed.Two"))
        ];
        VideoPlaybackSpeedOptions =
        [
            new(0.5d, localization.GetString("CustomTheme.GifSpeed.Half")),
            new(0.75d, localization.GetString("CustomTheme.GifSpeed.ThreeQuarters")),
            new(1d, localization.GetString("CustomTheme.GifSpeed.One")),
            new(1.25d, localization.GetString("CustomTheme.GifSpeed.OneQuarter")),
            new(1.5d, localization.GetString("CustomTheme.GifSpeed.OneHalf")),
            new(2d, localization.GetString("CustomTheme.GifSpeed.Two"))
        ];
    }

    public CustomThemeEditMode Mode { get; }
    public ObservableCollection<CustomThemeColorItemViewModel> Colors { get; }
    public IReadOnlyList<CustomThemeFitOption> ImageFitOptions { get; }
    public IReadOnlyList<CustomThemeFitOption> GifAnimationDirectionOptions { get; }
    public IReadOnlyList<CustomThemeSpeedOption> GifAnimationSpeedOptions { get; }
    public IReadOnlyList<CustomThemeSpeedOption> VideoPlaybackSpeedOptions { get; }
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
    public bool HasGifBackground => BackgroundAssetKinds.Detect(_settings.BackgroundAssetFileName) == BackgroundAssetKind.Gif;
    public bool HasVideoBackground => BackgroundAssetKinds.Detect(_settings.BackgroundAssetFileName) == BackgroundAssetKind.Video;
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
            _settings.ActivityPanelOpacity = opacity;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CategoriesPanelOpacityPercent));
            OnPropertyChanged(nameof(ProfilesPanelOpacityPercent));
            OnPropertyChanged(nameof(ProfileEditorPanelOpacityPercent));
            OnPropertyChanged(nameof(ActivityPanelOpacityPercent));
            ApplyDraft();
        }
    }
    public double HoverIntensityPercent
    {
        get => _settings.HoverIntensity;
        set
        {
            var intensity = Math.Clamp(value, 0, 100);
            if (Math.Abs(_settings.HoverIntensity - intensity) < 0.001) return;
            _settings.HoverIntensity = intensity;
            OnPropertyChanged();
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
    public double ActivityPanelOpacityPercent
    {
        get => _settings.ActivityPanelOpacity * 100;
        set { _settings.ActivityPanelOpacity = Math.Clamp(value / 100, 0, 1); OnPropertyChanged(); ApplyDraft(); }
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
        set
        {
            var fit = BackgroundImageFits.Normalize(value);
            if (_settings.ImageFit == fit) return;
            _settings.ImageFit = fit;
            OnPropertyChanged();
            ApplyDraft();
        }
    }
    public string GifAnimationDirection
    {
        get => _settings.GifAnimationDirection;
        set
        {
            var direction = GifAnimationDirections.Normalize(value);
            if (_settings.GifAnimationDirection == direction) return;
            _settings.GifAnimationDirection = direction;
            OnPropertyChanged();
            ApplyDraft();
        }
    }
    public double GifAnimationSpeed
    {
        get => _settings.GifAnimationSpeed;
        set
        {
            var speed = GifAnimationSpeeds.Normalize(value);
            if (Math.Abs(_settings.GifAnimationSpeed - speed) < 0.001) return;
            _settings.GifAnimationSpeed = speed;
            OnPropertyChanged();
            ApplyDraft();
        }
    }
    public double VideoPlaybackSpeed
    {
        get => _settings.VideoPlaybackSpeed;
        set
        {
            var speed = GifAnimationSpeeds.Normalize(value);
            if (Math.Abs(_settings.VideoPlaybackSpeed - speed) < 0.001) return;
            _settings.VideoPlaybackSpeed = speed;
            OnPropertyChanged();
            ApplyDraft();
        }
    }
    public bool VideoAudioEnabled
    {
        get => _settings.VideoAudioEnabled;
        set
        {
            if (_settings.VideoAudioEnabled == value) return;
            _settings.VideoAudioEnabled = value;
            OnPropertyChanged();
            ApplyDraft();
        }
    }
    public bool ImageFlipHorizontal
    {
        get => _settings.ImageFlipHorizontal;
        set
        {
            if (_settings.ImageFlipHorizontal == value) return;
            _settings.ImageFlipHorizontal = value;
            OnPropertyChanged();
            ApplyDraft();
        }
    }
    public bool ImageFlipVertical
    {
        get => _settings.ImageFlipVertical;
        set
        {
            if (_settings.ImageFlipVertical == value) return;
            _settings.ImageFlipVertical = value;
            OnPropertyChanged();
            ApplyDraft();
        }
    }
    public string Warning { get => _warning; set => SetProperty(ref _warning, value); }

    public void SetBackground(string? assetFileName, string? previewPath)
    {
        _settings.BackgroundAssetFileName = assetFileName;
        _settings.PreviewBackgroundPath = previewPath;
        OnPropertyChanged(nameof(BackgroundFileName));
        OnPropertyChanged(nameof(HasBackground));
        OnPropertyChanged(nameof(HasGifBackground));
        OnPropertyChanged(nameof(HasVideoBackground));
        ApplyDraft();
    }

    public void ClearTemporaryBackground()
    {
        _settings.BackgroundAssetFileName = null;
        _settings.PreviewBackgroundPath = null;
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
        OnPropertyChanged(nameof(HoverIntensityPercent));
        OnPropertyChanged(nameof(CategoriesPanelOpacityPercent));
        OnPropertyChanged(nameof(ProfilesPanelOpacityPercent));
        OnPropertyChanged(nameof(ProfileEditorPanelOpacityPercent));
        OnPropertyChanged(nameof(ActivityPanelOpacityPercent));
        OnPropertyChanged(nameof(BackgroundOpacityPercent));
        OnPropertyChanged(nameof(DarkOverlayPercent));
        OnPropertyChanged(nameof(ImageFit));
        OnPropertyChanged(nameof(GifAnimationDirection));
        OnPropertyChanged(nameof(GifAnimationSpeed));
        OnPropertyChanged(nameof(VideoPlaybackSpeed));
        OnPropertyChanged(nameof(VideoAudioEnabled));
        OnPropertyChanged(nameof(ImageFlipHorizontal));
        OnPropertyChanged(nameof(ImageFlipVertical));
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

}

public sealed record CustomThemeFitOption(string Value, string DisplayName);
public sealed record CustomThemeSpeedOption(double Value, string DisplayName);
