using SwitchBoard.Models.Profiles;
using SwitchBoard.Services.ApplicationLifecycle;

namespace SwitchBoard.Services.Execution;

public sealed class ProfileCompletionBehavior(
    IApplicationLifetime applicationLifetime) : IProfileCompletionBehavior
{
    public void HandleSuccessfulCompletion(ProfileDefinition profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.CloseSwitchBoardAfterSuccessfulCompletion)
        {
            applicationLifetime.Shutdown();
        }
    }
}
