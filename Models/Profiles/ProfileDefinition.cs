using SwitchBoard.Models.Actions;

namespace SwitchBoard.Models.Profiles;

public sealed class ProfileDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CategoryId { get; set; }

    public string Name { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public bool CloseSwitchBoardAfterSuccessfulCompletion { get; set; }

    public List<ActionDefinition> Actions { get; set; } = [];
}
