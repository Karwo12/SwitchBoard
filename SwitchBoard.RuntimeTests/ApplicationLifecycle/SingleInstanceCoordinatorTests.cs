using SwitchBoard.Services.ApplicationLifecycle;

namespace SwitchBoard.RuntimeTests.ApplicationLifecycle;

public sealed class SingleInstanceCoordinatorTests
{
    [Fact]
    public void FirstInstance_AcquiresLock()
    {
        var names = CreateNames();
        using var first = SingleInstanceCoordinator.TryAcquire(names.Mutex, names.Event);

        Assert.NotNull(first);
        Assert.False(first.IsDisposed);
    }

    [Fact]
    public void SecondInstance_DetectsExistingLock()
    {
        var names = CreateNames();
        using var first = SingleInstanceCoordinator.TryAcquire(names.Mutex, names.Event);

        using var second = SingleInstanceCoordinator.TryAcquire(names.Mutex, names.Event);

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public void SecondInstance_DoesNotContinueFullInitialization()
    {
        var names = CreateNames();
        using var first = SingleInstanceCoordinator.TryAcquire(names.Mutex, names.Event);
        var fullInitializationCalls = 0;

        var second = SingleInstanceCoordinator.TryAcquire(names.Mutex, names.Event);
        if (second is not null)
        {
            fullInitializationCalls++;
            second.Dispose();
        }

        Assert.NotNull(first);
        Assert.Equal(0, fullInitializationCalls);
    }

    [Fact]
    public async Task ActivationSignal_InvokesPrimaryCallback()
    {
        var names = CreateNames();
        using var first = SingleInstanceCoordinator.TryAcquire(names.Mutex, names.Event);
        Assert.NotNull(first);

        var activation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.StartListening(() => activation.TrySetResult(true));

        Assert.True(SingleInstanceCoordinator.TrySignalExisting(names.Event));
        Assert.True(await activation.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Dispose_ReleasesLockAndSignal()
    {
        var names = CreateNames();
        var first = SingleInstanceCoordinator.TryAcquire(names.Mutex, names.Event);
        Assert.NotNull(first);

        first.Dispose();
        Assert.False(SingleInstanceCoordinator.TrySignalExisting(names.Event));

        using var reacquired = SingleInstanceCoordinator.TryAcquire(names.Mutex, names.Event);
        Assert.NotNull(reacquired);
    }

    private static (string Mutex, string Event) CreateNames()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return ($"Local\\SwitchBoard.RuntimeTests.Mutex.{suffix}",
            $"Local\\SwitchBoard.RuntimeTests.Event.{suffix}");
    }
}
