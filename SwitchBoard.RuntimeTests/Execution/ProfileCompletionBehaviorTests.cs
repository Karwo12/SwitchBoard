using SwitchBoard.Models.Profiles;
using SwitchBoard.Services.ApplicationLifecycle;
using SwitchBoard.Services.Execution;

namespace SwitchBoard.RuntimeTests.Execution;

public sealed class ProfileCompletionBehaviorTests
{
    [Fact]
    public void SuccessfulCompletion_DoesNotShutdownWhenProfileOptionIsDisabled()
    {
        var lifetime = new TestApplicationLifetime();
        var behavior = new ProfileCompletionBehavior(lifetime);

        behavior.HandleSuccessfulCompletion(new ProfileDefinition
        {
            Name = "Keep open",
            CloseSwitchBoardAfterSuccessfulCompletion = false
        });

        Assert.Equal(0, lifetime.ShutdownCount);
    }

    [Fact]
    public void SuccessfulCompletion_ShutsDownOnlyWhenProfileOptionIsEnabled()
    {
        var lifetime = new TestApplicationLifetime();
        var behavior = new ProfileCompletionBehavior(lifetime);

        behavior.HandleSuccessfulCompletion(new ProfileDefinition
        {
            Name = "Close",
            CloseSwitchBoardAfterSuccessfulCompletion = true
        });

        Assert.Equal(1, lifetime.ShutdownCount);
    }

    private sealed class TestApplicationLifetime : IApplicationLifetime
    {
        public int ShutdownCount { get; private set; }
        public void Shutdown() => ShutdownCount++;
    }
}
