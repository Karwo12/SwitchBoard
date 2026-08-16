using SwitchBoard.Data;
using SwitchBoard.Services.ApplicationLifecycle;

namespace SwitchBoard.Services.Execution;

public sealed class ProfileCompletionBehavior(
    UserSettings settings,
    IApplicationLifetime applicationLifetime) : IProfileCompletionBehavior
{
    public void HandleSuccessfulCompletion()
    {
        if (settings.CloseSwitchBoardAfterProfileFinishes)
        {
            applicationLifetime.Shutdown();
        }
    }
}
