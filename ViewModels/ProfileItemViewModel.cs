using System.Collections.ObjectModel;
using SwitchBoard.Localization;
using SwitchBoard.Models.Profiles;

namespace SwitchBoard.ViewModels;

public sealed class ProfileItemViewModel : ObservableObject
{
    private string _name;
    private string _editName;
    private bool _isEditing;
    private bool _closeSwitchBoardAfterSuccessfulCompletion;

    public ProfileItemViewModel(ProfileDefinition profile, ILocalizationService localizationService)
    {
        Id = profile.Id;
        CategoryId = profile.CategoryId;
        _name = profile.Name;
        _editName = profile.Name;
        _closeSwitchBoardAfterSuccessfulCompletion = profile.CloseSwitchBoardAfterSuccessfulCompletion;
        SortOrder = profile.SortOrder;
        Actions = new ObservableCollection<ActionItemViewModel>(
            profile.Actions
                .OrderBy(action => action.SortOrder)
                .Select(action => new ActionItemViewModel(action, localizationService)));
    }

    public Guid Id { get; }

    public Guid CategoryId { get; }

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

    public ObservableCollection<ActionItemViewModel> Actions { get; }

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
        Actions = Actions.Select(action => action.ToModel()).ToList()
    };
}
