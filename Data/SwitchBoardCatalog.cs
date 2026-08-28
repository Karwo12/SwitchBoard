using SwitchBoard.Models.Categories;
using SwitchBoard.Models.Profiles;
using System.Text.Json.Serialization;

namespace SwitchBoard.Data;

public sealed class SwitchBoardCatalog
{
    public int SchemaVersion { get; set; } = CatalogSchema.CurrentVersion;

    public List<CategoryDefinition> Categories { get; set; } = [];

    public List<ProfileDefinition> Profiles { get; set; } = [];

    /// <summary>
    /// Optional shared order for top-level categories and root profiles.
    /// A null value represents catalogs written before mixed root ordering
    /// existed and is deliberately kept distinct from an empty order.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<RootNavigationItemDefinition>? RootNavigationOrder { get; set; }

    public static SwitchBoardCatalog Empty() => new();
}
