using SwitchBoard.Models.Actions;

namespace SwitchBoard.Models.Profiles;

public sealed class ProfileDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CategoryId { get; set; }

    public string Name { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public bool CloseSwitchBoardAfterSuccessfulCompletion { get; set; }

    /// <summary>Optional visual marker only; it never affects profile execution.</summary>
    public string? Color { get; set; }

    /// <summary>Optional identifier from SwitchBoard's small built-in icon set.</summary>
    public string? Icon { get; set; }

    public List<ActionDefinition> Actions { get; set; } = [];
}
