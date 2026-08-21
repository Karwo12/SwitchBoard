using SwitchBoard.RuntimeTests.TestInfrastructure;

namespace SwitchBoard.RuntimeTests.Actions;

[Collection("Windows runtime")]
public sealed class DisplayActionTests : RuntimeTestBase
{
    [EnvironmentFact("Display")]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task Display_Discovery_ReturnsModesForEveryMonitor()
    {
        using var context = new RuntimeTestContext();
        var displays = await context.DisplayManager.GetDisplaysAsync();

        Assert.NotEmpty(displays);
        Assert.All(displays, display => Assert.NotEmpty(display.Modes));
    }

    [EnvironmentFact("Display")]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task Display_NativeApplyVerify_PreservesTheCurrentSafeMode()
    {
        using var context = new RuntimeTestContext();
        var display = (await context.DisplayManager.GetDisplaysAsync()).FirstOrDefault();
        Assert.NotNull(display);
        var state = await context.DisplayManager.GetCurrentStateAsync(display.DeviceId, display.DeviceName);
        try
        {
            await context.DisplayManager.ApplyTemporaryAsync(state);
            var verified = await context.DisplayManager.GetCurrentStateAsync(display.DeviceId, display.DeviceName);
            Assert.Equal(state.Width, verified.Width);
            Assert.Equal(state.Height, verified.Height);
            Assert.Equal(state.RefreshRate, verified.RefreshRate);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return;
        }
        finally
        {
            try { await context.DisplayManager.RestoreAsync(state); } catch { }
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DisplayConfigure_KeepConfirmation_AppliesTheRequestedMode()
    {
        var previous = SimulatedPrevious();
        var manager = new TestDisplayManager(previous);
        var result = await new DisplayConfigureActionHandler(manager, new TestDisplayConfirmationService(true))
            .ExecuteAsync(SimulatedTarget(), new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        Assert.True(result.IsSuccessful);
        Assert.Equal(2560, manager.State.Width);
        Assert.Equal(144, manager.State.RefreshRate);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DisplayConfigure_RevertConfirmation_LeavesThePreviousMode()
    {
        var previous = SimulatedPrevious();
        var manager = new TestDisplayManager(previous);
        var result = await new DisplayConfigureActionHandler(manager, new TestDisplayConfirmationService(false))
            .ExecuteAsync(SimulatedTarget(), new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        Assert.False(result.IsSuccessful);
        Assert.Equal(previous, manager.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DisplayConfigure_ConfirmationTimeout_RevertsTheMode()
    {
        var previous = SimulatedPrevious();
        var manager = new TestDisplayManager(previous);
        var result = await new DisplayConfigureActionHandler(manager,
                new TestDisplayConfirmationService(false, TimeSpan.FromMilliseconds(350)))
            .ExecuteAsync(SimulatedTarget(), new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        Assert.False(result.IsSuccessful);
        Assert.Equal(previous, manager.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DisplayConfigure_Restore_RestoresTheCapturedMode()
    {
        var previous = SimulatedPrevious();
        var manager = new TestDisplayManager(previous);
        var handler = new DisplayConfigureActionHandler(manager, new TestDisplayConfirmationService(true));
        var action = SimulatedTarget();
        var captured = await handler.CaptureStateAsync(action, new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
        await handler.ExecuteAsync(action, new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
        await handler.RestoreAsync(action, captured!, new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(previous, manager.State);
    }

    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    [EnvironmentFact("AlternateDisplayMode")]
    public async Task DisplayConfigure_AlternateModeKeep_AppliesAndRestoresTheMode()
    {
        using var context = new RuntimeTestContext();
        var (display, original, alternate) = await FindAlternateModeAsync(context);
        var action = CreateDisplayAction(display, alternate);
        try
        {
            var result = await new DisplayConfigureActionHandler(context.DisplayManager,
                    new TestDisplayConfirmationService(true))
                .ExecuteAsync(action, new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
            if (!result.IsSuccessful) return;
            var changed = await context.DisplayManager.GetCurrentStateAsync(display.DeviceId, display.DeviceName);
            Assert.Equal(alternate.Width, changed.Width);
            Assert.Equal(alternate.Height, changed.Height);
            Assert.Equal(alternate.RefreshRate, changed.RefreshRate);
        }
        finally
        {
            await context.DisplayManager.PersistAsync(original);
        }
    }

    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    [EnvironmentFact("AlternateDisplayMode")]
    public async Task DisplayConfigure_AlternateModeRevert_RestoresThePreviousMode()
    {
        using var context = new RuntimeTestContext();
        var (display, original, alternate) = await FindAlternateModeAsync(context);
        var result = await new DisplayConfigureActionHandler(context.DisplayManager,
                new TestDisplayConfirmationService(false))
            .ExecuteAsync(CreateDisplayAction(display, alternate), new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
        var state = await context.DisplayManager.GetCurrentStateAsync(display.DeviceId, display.DeviceName);

        Assert.False(result.IsSuccessful);
        Assert.Equal(original.Width, state.Width);
        Assert.Equal(original.Height, state.Height);
        Assert.Equal(original.RefreshRate, state.RefreshRate);
    }

    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    [EnvironmentFact("AlternateDisplayMode")]
    public async Task DisplayConfigure_AlternateModeTimeout_RevertsThePreviousMode()
    {
        using var context = new RuntimeTestContext();
        var (display, original, alternate) = await FindAlternateModeAsync(context);
        var result = await new DisplayConfigureActionHandler(context.DisplayManager,
                new TestDisplayConfirmationService(false, TimeSpan.FromMilliseconds(350)))
            .ExecuteAsync(CreateDisplayAction(display, alternate), new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
        var state = await context.DisplayManager.GetCurrentStateAsync(display.DeviceId, display.DeviceName);

        Assert.False(result.IsSuccessful);
        Assert.Equal(original.Width, state.Width);
        Assert.Equal(original.Height, state.Height);
        Assert.Equal(original.RefreshRate, state.RefreshRate);
    }

    private static DisplayModeState SimulatedPrevious() =>
        new("DISPLAY-TEST", "MONITOR-TEST", "Test monitor", 1920, 1080, 60, 32, 0, 0, 0, 0);

    private static ActionDefinition SimulatedTarget() => Action(ActionTypeIds.DisplayConfigure, new JsonObject
    {
        [ActionParameterNames.DisplayDeviceName] = "MONITOR-TEST",
        [ActionParameterNames.DisplayDeviceId] = "DISPLAY-TEST",
        [ActionParameterNames.DisplayName] = "Test monitor",
        [ActionParameterNames.DisplayWidth] = 2560,
        [ActionParameterNames.DisplayHeight] = 1440,
        [ActionParameterNames.DisplayRefreshRate] = 144
    });

    private static ActionDefinition CreateDisplayAction(DisplayCandidate display, DisplayModeCandidate mode) =>
        Action(ActionTypeIds.DisplayConfigure, new JsonObject
        {
            [ActionParameterNames.DisplayDeviceName] = display.DeviceName,
            [ActionParameterNames.DisplayDeviceId] = display.DeviceId,
            [ActionParameterNames.DisplayName] = display.DisplayName,
            [ActionParameterNames.DisplayWidth] = mode.Width,
            [ActionParameterNames.DisplayHeight] = mode.Height,
            [ActionParameterNames.DisplayRefreshRate] = mode.RefreshRate
        });

    private static async Task<(DisplayCandidate Display, DisplayModeState Original, DisplayModeCandidate Alternate)>
        FindAlternateModeAsync(RuntimeTestContext context)
    {
        var displays = await context.DisplayManager.GetDisplaysAsync();
        var display = displays.FirstOrDefault(item => item.Modes.Any(mode =>
            mode.Width != item.CurrentWidth || mode.Height != item.CurrentHeight || mode.RefreshRate != item.CurrentRefreshRate));
        Assert.NotNull(display);
        var original = await context.DisplayManager.GetCurrentStateAsync(display.DeviceId, display.DeviceName);
        var alternate = display.Modes
            .Where(mode => mode.Width != original.Width || mode.Height != original.Height || mode.RefreshRate != original.RefreshRate)
            .OrderByDescending(mode => mode.Width == original.Width && mode.Height == original.Height)
            .ThenBy(mode => Math.Abs(mode.RefreshRate - original.RefreshRate))
            .First();
        return (display, original, alternate);
    }
}
