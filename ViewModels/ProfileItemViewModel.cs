using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using SwitchBoard.Localization;
using SwitchBoard.Models.Profiles;
using SwitchBoard.Services.Discovery;

namespace SwitchBoard.ViewModels;

public sealed class ProfileItemViewModel : ObservableObject
{
    private string _name;
    private string _editName;
    private string _settingsDisplayName;
    private bool _isEditing;
    private bool _closeSwitchBoardAfterSuccessfulCompletion;
    private bool _closeSwitchBoardAfterSuccessfulRestore;
    private string? _color;
    private string? _iconSourcePath;
    private Guid? _iconSourceActionId;
    private ImageSource? _iconImage;
    private int _iconLoadVersion;
    private readonly FileIconCache _iconCache;
    private readonly Dispatcher? _uiDispatcher;
    private readonly ObservableCollection<ActionItemViewModel> _editorActions = [];
    private ProfileExecutionState _executionState;

    public ProfileItemViewModel(ProfileDefinition profile, ILocalizationService localizationService,
        FileIconCache? iconCache = null)
    {
        _iconCache = iconCache ?? FileIconCache.Shared;
        _uiDispatcher = Application.Current?.Dispatcher;
        Id = profile.Id;
        CategoryId = profile.CategoryId;
        _name = profile.Name;
        _editName = profile.Name;
        _settingsDisplayName = profile.Name;
        _closeSwitchBoardAfterSuccessfulCompletion = profile.CloseSwitchBoardAfterSuccessfulCompletion;
        _closeSwitchBoardAfterSuccessfulRestore = profile.CloseSwitchBoardAfterSuccessfulRestore;
        _color = profile.Color;
        _iconSourcePath = NormalizeIconSourcePath(profile.IconSource);
        _iconSourceActionId = NormalizeIconSourceActionId(profile.IconSource);
        SortOrder = profile.SortOrder;
        Actions = new ObservableCollection<ActionItemViewModel>(
            profile.Actions
                .OrderBy(action => action.SortOrder)
                .Select(action => new ActionItemViewModel(action, localizationService, iconCache: _iconCache)));
        PostRestoreActions = new ObservableCollection<ActionItemViewModel>(
            profile.PostRestoreActions
                .OrderBy(action => action.SortOrder)
                .Select(action => new ActionItemViewModel(action, localizationService, iconCache: _iconCache)));
        EditorActions = new ReadOnlyObservableCollection<ActionItemViewModel>(_editorActions);
        Actions.CollectionChanged += ActionsOnCollectionChanged;
        PostRestoreActions.CollectionChanged += ActionsOnCollectionChanged;
        foreach (var action in Actions) SubscribeToAction(action);
        foreach (var action in PostRestoreActions) SubscribeToAction(action);
        SynchronizeEditorActions();
        RefreshIconImage();
    }

    public Guid Id { get; }

    public Guid CategoryId { get; private set; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string EditName
    {
        get => _editName;
        set => SetProperty(ref _editName, value);
    }

    /// <summary>
    /// Display-only label used by the settings profile selector. It is derived
    /// from the existing profile and category data and is not persisted.
    /// </summary>
    public string SettingsDisplayName
    {
        get => _settingsDisplayName;
        private set => SetProperty(ref _settingsDisplayName, value);
    }

    public bool IsEditing
    {
        get => _isEditing;
        private set => SetProperty(ref _isEditing, value);
    }

    public int SortOrder { get; set; }

    public bool CloseSwitchBoardAfterSuccessfulCompletion
    {
        get => _closeSwitchBoardAfterSuccessfulCompletion;
        set => SetProperty(ref _closeSwitchBoardAfterSuccessfulCompletion, value);
    }

    public bool CloseSwitchBoardAfterSuccessfulRestore
    {
        get => _closeSwitchBoardAfterSuccessfulRestore;
        set => SetProperty(ref _closeSwitchBoardAfterSuccessfulRestore, value);
    }

    /// <summary>Optional presentation color persisted with the profile.</summary>
    public string? Color
    {
        get => _color;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (!SetProperty(ref _color, normalized)) return;
            OnPropertyChanged(nameof(HasAppearance));
        }
    }

    /// <summary>Optional full path to the EXE or ICO selected as the profile icon source.</summary>
    public string? IconSourcePath => _iconSourcePath;

    /// <summary>Stable ID of the selected action when the profile follows an action icon.</summary>
    public Guid? IconSourceActionId => _iconSourceActionId;

    public bool UsesActionIcon => IconSourceActionId is not null;

    public ActionItemViewModel? IconSourceAction => IconSourceActionId is { } actionId
        ? Actions.Concat(PostRestoreActions).FirstOrDefault(action => action.Id == actionId)
        : null;

    public string IconSourceDisplayName => IconSourceAction?.DisplayName ??
        (UsesActionIcon ? "-" : string.IsNullOrWhiteSpace(IconSourcePath) ? "-" : Path.GetFileName(IconSourcePath));

    public ImageSource? IconImage
    {
        get => _iconImage;
        private set
        {
            if (!SetProperty(ref _iconImage, value)) return;
            OnPropertyChanged(nameof(HasIconImage));
        }
    }

    public bool HasIconImage => IconImage is not null;

    public bool HasAppearance => !string.IsNullOrWhiteSpace(Color) || UsesActionIcon ||
                                 !string.IsNullOrWhiteSpace(IconSourcePath);

    /// <summary>Sets a file-backed profile icon and replaces any action-backed icon.</summary>
    public void SetIconSourcePath(string? sourcePath)
    {
        var normalized = NormalizeIconSourcePath(sourcePath);
        if (string.Equals(_iconSourcePath, normalized, StringComparison.OrdinalIgnoreCase) && _iconSourceActionId is null) return;

        _iconSourcePath = normalized;
        _iconSourceActionId = null;
        NotifyIconSourceChanged();
    }

    /// <summary>Uses the selected profile action's shared icon source without copying the bitmap.</summary>
    public void SetIconSourceAction(Guid? actionId)
    {
        Guid? normalized = actionId is { } value && value != Guid.Empty ? value : null;
        if (_iconSourceActionId == normalized && _iconSourcePath is null) return;

        _iconSourceActionId = normalized;
        _iconSourcePath = null;
        NotifyIconSourceChanged();
    }

    public void ClearIconSource()
    {
        if (_iconSourcePath is null && _iconSourceActionId is null) return;
        _iconSourcePath = null;
        _iconSourceActionId = null;
        NotifyIconSourceChanged();
    }

    /// <summary>Neutral navigation fallback when no action or file icon is currently available.</summary>
    public string IconPathData => "M3,2 H17 A2,2 0 0 1 19,4 V16 A2,2 0 0 1 17,18 H3 A2,2 0 0 1 1,16 V4 A2,2 0 0 1 3,2 Z M5,6 H15 M5,10 H12";

    public ObservableCollection<ActionItemViewModel> Actions { get; }

    /// <summary>Actions that run only after a successful regular restore.</summary>
    public ObservableCollection<ActionItemViewModel> PostRestoreActions { get; }

    /// <summary>Single virtualized editor presentation; ordering remains isolated in the two source collections.</summary>
    public ReadOnlyObservableCollection<ActionItemViewModel> EditorActions { get; }

    public ProfileExecutionState ExecutionState
    {
        get => _executionState;
        private set
        {
            if (!SetProperty(ref _executionState, value)) return;
            OnPropertyChanged(nameof(IsExecuting));
            OnPropertyChanged(nameof(IsRestoring));
            OnPropertyChanged(nameof(HasExecutionError));
        }
    }
    public bool IsExecuting => ExecutionState == ProfileExecutionState.Executing;
    public bool IsRestoring => ExecutionState == ProfileExecutionState.Restoring;
    public bool HasExecutionError => ExecutionState == ProfileExecutionState.Error;

    public void SetExecutionState(ProfileExecutionState state) => ExecutionState = state;
    public void ClearExecutionError()
    {
        if (HasExecutionError) ExecutionState = ProfileExecutionState.Normal;
    }

    public void MoveToCategory(Guid categoryId)
    {
        if (CategoryId == categoryId) return;
        CategoryId = categoryId;
        OnPropertyChanged(nameof(CategoryId));
    }

    internal void UpdateSettingsDisplayName(string displayName) => SettingsDisplayName = displayName;

    public void BeginEdit()
    {
        EditName = Name;
        IsEditing = true;
    }

    public void CommitEdit()
    {
        if (!IsEditing)
        {
            return;
        }

        var candidate = EditName.Trim();
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            Name = candidate;
        }
        else
        {
            EditName = Name;
        }

        IsEditing = false;
    }

    public void CancelEdit()
    {
        EditName = Name;
        IsEditing = false;
    }

    public ProfileDefinition ToModel() => new()
    {
        Id = Id,
        CategoryId = CategoryId,
        Name = Name.Trim(),
        SortOrder = SortOrder,
        CloseSwitchBoardAfterSuccessfulCompletion = CloseSwitchBoardAfterSuccessfulCompletion,
        CloseSwitchBoardAfterSuccessfulRestore = CloseSwitchBoardAfterSuccessfulRestore,
        Color = Color,
        // Built-in symbols are legacy-only. New saves retain exactly one explicit source,
        // either a file path or a stable action ID.
        Icon = null,
        IconSource = BuildIconSourceDefinition(),
        Actions = Actions.Select(action => action.ToModel()).ToList(),
        PostRestoreActions = PostRestoreActions.Select(action => action.ToModel()).ToList()
    };

    private void RefreshIconImage()
    {
        var request = ++_iconLoadVersion;
        if (UsesActionIcon)
        {
            IconImage = IconSourceAction?.ApplicationIcon ?? IconSourceAction?.ActionFallbackIcon;
            return;
        }

        var sourcePath = IconSourcePath;
        if (sourcePath is null)
        {
            IconImage = null;
            return;
        }

        _ = LoadIconImageAsync(sourcePath, request);
    }

    private async Task LoadIconImageAsync(string sourcePath, int request)
    {
        ImageSource? icon;
        try { icon = await _iconCache.GetSmallIconAsync(sourcePath); }
        catch { icon = null; }
        if (request != _iconLoadVersion || !string.Equals(sourcePath, IconSourcePath, StringComparison.OrdinalIgnoreCase))
            return;
        if (_uiDispatcher is { HasShutdownStarted: false, HasShutdownFinished: false } dispatcher &&
            !dispatcher.CheckAccess())
        {
            await dispatcher.InvokeAsync(() =>
            {
                if (request == _iconLoadVersion &&
                    string.Equals(sourcePath, IconSourcePath, StringComparison.OrdinalIgnoreCase))
                    IconImage = icon;
            });
            return;
        }

        if (request == _iconLoadVersion && string.Equals(sourcePath, IconSourcePath, StringComparison.OrdinalIgnoreCase))
            IconImage = icon;
    }

    private ProfileIconSourceDefinition? BuildIconSourceDefinition() => IconSourceActionId is { } actionId
        ? new ProfileIconSourceDefinition
        {
            Type = ProfileIconSourceDefinition.ActionSourceType,
            ActionId = actionId
        }
        : string.IsNullOrWhiteSpace(IconSourcePath) ? null : new ProfileIconSourceDefinition
        {
            Type = ProfileIconSourceDefinition.FileSourceType,
            Path = IconSourcePath
        };

    private void NotifyIconSourceChanged()
    {
        OnPropertyChanged(nameof(IconSourcePath));
        OnPropertyChanged(nameof(IconSourceActionId));
        OnPropertyChanged(nameof(IconSourceAction));
        OnPropertyChanged(nameof(UsesActionIcon));
        OnPropertyChanged(nameof(IconSourceDisplayName));
        OnPropertyChanged(nameof(HasAppearance));
        RefreshIconImage();
    }

    private void ActionsOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var action in e.OldItems.OfType<ActionItemViewModel>()) UnsubscribeFromAction(action);
        }
        if (e.NewItems is not null)
        {
            foreach (var action in e.NewItems.OfType<ActionItemViewModel>()) SubscribeToAction(action);
        }
        if (UsesActionIcon)
        {
            OnPropertyChanged(nameof(IconSourceAction));
            OnPropertyChanged(nameof(IconSourceDisplayName));
            RefreshIconImage();
        }
        SynchronizeEditorActions();
    }

    private void SynchronizeEditorActions()
    {
        var ordered = Actions.Concat(PostRestoreActions).ToList();
        for (var index = 0; index < ordered.Count; index++)
        {
            var action = ordered[index];
            var currentIndex = _editorActions.IndexOf(action);
            if (currentIndex == index) continue;
            if (currentIndex >= 0) _editorActions.Move(currentIndex, index);
            else _editorActions.Insert(index, action);
        }
        while (_editorActions.Count > ordered.Count)
            _editorActions.RemoveAt(_editorActions.Count - 1);

        for (var index = 0; index < Actions.Count; index++)
            Actions[index].IsPostRestoreSectionStart = false;
        for (var index = 0; index < PostRestoreActions.Count; index++)
            PostRestoreActions[index].IsPostRestoreSectionStart = index == 0;
    }

    private void SubscribeToAction(ActionItemViewModel action) => action.PropertyChanged += IconSourceActionOnPropertyChanged;

    private void UnsubscribeFromAction(ActionItemViewModel action) => action.PropertyChanged -= IconSourceActionOnPropertyChanged;

    private void IconSourceActionOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ActionItemViewModel action || action.Id != IconSourceActionId) return;
        if (e.PropertyName is nameof(ActionItemViewModel.ApplicationIcon) or nameof(ActionItemViewModel.ActionFallbackIcon))
            RefreshIconImage();
        if (e.PropertyName is nameof(ActionItemViewModel.DisplayName) or nameof(ActionItemViewModel.Summary))
            OnPropertyChanged(nameof(IconSourceDisplayName));
    }

    private static string? NormalizeIconSourcePath(ProfileIconSourceDefinition? source) =>
        source is not null && string.Equals(source.Type, ProfileIconSourceDefinition.FileSourceType,
            StringComparison.OrdinalIgnoreCase) ? NormalizeIconSourcePath(source.Path) : null;

    private static Guid? NormalizeIconSourceActionId(ProfileIconSourceDefinition? source) =>
        source is not null && string.Equals(source.Type, ProfileIconSourceDefinition.ActionSourceType,
            StringComparison.OrdinalIgnoreCase) && source.ActionId is { } actionId && actionId != Guid.Empty
            ? actionId
            : null;

    private static string? NormalizeIconSourcePath(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return null;
        try
        {
            var trimmed = sourcePath.Trim();
            var extension = Path.GetExtension(trimmed);
            if (!Path.IsPathFullyQualified(trimmed) ||
                (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
                 !extension.Equals(".ico", StringComparison.OrdinalIgnoreCase))) return null;
            return Path.GetFullPath(trimmed);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
