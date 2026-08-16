using SwitchBoard.Models.Profiles;

namespace SwitchBoard.Services.Execution;

public interface IProfileCompletionBehavior
{
    void HandleSuccessfulCompletion(ProfileDefinition profile);
}
