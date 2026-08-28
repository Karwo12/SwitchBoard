namespace SwitchBoard.Models.Categories;

public sealed class CategoryDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public int SortOrder { get; set; }
}
