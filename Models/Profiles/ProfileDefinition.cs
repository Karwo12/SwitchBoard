using SwitchBoard.Models.Actions;
using System.Text.Json.Serialization;

namespace SwitchBoard.Models.Profiles;

public sealed class ProfileDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CategoryId { get; set; }

    public string Name { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public bool CloseSwitchBoardAfterSuccessfulCompletion { get; set; }

    public bool CloseSwitchBoardAfterSuccessfulRestore { get; set; }

    /// <summary>Optional visual marker only; it never affects profile execution.</summary>
    public string? Color { get; set; }

    /// <summary>Optional identifier from SwitchBoard's small built-in icon set.</summary>
    public string? Icon { get; set; }

    /// <summary>
    /// Optional user-selected icon source. Only the stable source reference is persisted;
    /// the bitmap is resolved through the shared icon cache at runtime.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProfileIconSourceDefinition? IconSource { get; set; }

    public List<ActionDefinition> Actions { get; set; } = [];

    /// <summary>
    /// Optional actions executed only after the profile's regular restore has completed successfully.
    /// Older catalogs do not contain this property and deserialize to an empty collection.
    /// </summary>
    public List<ActionDefinition> PostRestoreActions { get; set; } = [];
}
