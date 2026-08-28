using System.Collections.ObjectModel;
using SwitchBoard.Models.Categories;

namespace SwitchBoard.ViewModels;

public sealed class CategoryItemViewModel : ObservableObject
{
    private string _name;
    private string _editName;
    private bool _isEditing;
    private bool _isExpanded = true;

    public CategoryItemViewModel(CategoryDefinition category)
    {
        Id = category.Id;
        _name = category.Name;
        _editName = category.Name;
        SortOrder = category.SortOrder;
    }

    public Guid Id { get; }

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

    /// <summary>
    /// Profiles displayed below this folder in the Home navigation. The catalog keeps
    /// the relationship through ProfileDefinition.CategoryId; this collection is
    /// rebuilt by MainWindowViewModel and is deliberately not persisted.
    /// </summary>
    public ObservableCollection<ProfileItemViewModel> Profiles { get; } = [];

    /// <summary>
    /// Presentation-only subset used by the Home search field. It never changes
    /// the category relationship or its persisted ordering.
    /// </summary>
    public ObservableCollection<ProfileItemViewModel> VisibleProfiles { get; } = [];

    /// <summary>
    /// Session-only state for the Home folder presentation.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

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

    public CategoryDefinition ToModel() => new()
    {
        Id = Id,
        Name = Name.Trim(),
        SortOrder = SortOrder
    };
}
