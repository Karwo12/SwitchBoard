using SwitchBoard.Models.Categories;

namespace SwitchBoard.ViewModels;

public sealed class CategoryItemViewModel : ObservableObject
{
    private string _name;
    private string _editName;
    private bool _isEditing;

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
