using System.Collections.ObjectModel;
using SwitchBoard.Localization;
using SwitchBoard.Models.Profiles;

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
    private string? _icon;
    private ProfileExecutionState _executionState;

    public ProfileItemViewModel(ProfileDefinition profile, ILocalizationService localizationService)
    {
        Id = profile.Id;
        CategoryId = profile.CategoryId;
        _name = profile.Name;
        _editName = profile.Name;
        _settingsDisplayName = profile.Name;
        _closeSwitchBoardAfterSuccessfulCompletion = profile.CloseSwitchBoardAfterSuccessfulCompletion;
        _closeSwitchBoardAfterSuccessfulRestore = profile.CloseSwitchBoardAfterSuccessfulRestore;
        _color = profile.Color;
        _icon = profile.Icon;
        SortOrder = profile.SortOrder;
        Actions = new ObservableCollection<ActionItemViewModel>(
            profile.Actions
                .OrderBy(action => action.SortOrder)
                .Select(action => new ActionItemViewModel(action, localizationService)));
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

    /// <summary>Optional identifier from the small built-in profile icon set.</summary>
    public string? Icon
    {
        get => _icon;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) || string.Equals(value, "none", StringComparison.OrdinalIgnoreCase)
                ? null : value.Trim();
            if (!SetProperty(ref _icon, normalized)) return;
            OnPropertyChanged(nameof(HasAppearance));
            OnPropertyChanged(nameof(IconPathData));
        }
    }

    public bool HasAppearance => !string.IsNullOrWhiteSpace(Color) || !string.IsNullOrWhiteSpace(Icon);

    /// <summary>Small vector paths used only in the profile navigation.</summary>
    public string IconPathData => Icon?.ToLowerInvariant() switch
    {
        "bolt" => "M13,1 L3,10 H9 L7,17 L17,7 H11 Z",
        "gamepad" => "M5,7 H15 C17.2,7 18.8,9 18.3,11.2 L17.5,14.2 C17.1,15.8 15,16.3 13.9,15 L12,13.2 H8 L6.1,15 C5,16.3 2.9,15.8 2.5,14.2 L1.7,11.2 C1.2,9 2.8,7 5,7 Z M5,10 V13 M3.5,11.5 H6.5 M14.5,10.5 H14.6 M16,12 H16.1",
        "briefcase" => "M2,6 H18 V16 H2 Z M7,6 V4 H13 V6 M2,10 H18 M8,10 V12 H12 V10",
        "moon" => "M14.8,14.8 A7,7 0 1 1 9.2,2.2 A5.6,5.6 0 1 0 14.8,14.8 Z",
        "monitor" => "M2,3 H18 V14 H2 Z M7,18 H13 M10,14 V18",
        _ => string.Empty
    };

    public ObservableCollection<ActionItemViewModel> Actions { get; }

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
        Icon = Icon,
        Actions = Actions.Select(action => action.ToModel()).ToList()
    };
}
