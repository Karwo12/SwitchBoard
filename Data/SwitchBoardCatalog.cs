using SwitchBoard.Models.Categories;
using SwitchBoard.Models.Profiles;

namespace SwitchBoard.Data;

public sealed class SwitchBoardCatalog
{
    public int SchemaVersion { get; set; } = CatalogSchema.CurrentVersion;

    public List<CategoryDefinition> Categories { get; set; } = [];

    public List<ProfileDefinition> Profiles { get; set; } = [];

    public static SwitchBoardCatalog Empty() => new();
}
