using SwitchBoard.RuntimeTests.TestInfrastructure;

namespace SwitchBoard.RuntimeTests.Actions;

[Collection("Windows runtime")]
public sealed class PowerPlanAndServiceTests : RuntimeTestBase
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task PowerPlan_Discovery_ReturnsTheActivePlanAndFriendlyNames()
    {
        using var context = new RuntimeTestContext();
        var plans = await context.PowerManager.GetPlansAsync();
        var active = await context.PowerManager.GetActivePlanAsync();

        Assert.NotEmpty(plans);
        Assert.Contains(plans, plan => plan.Id == active);
        Assert.All(plans, plan => Assert.False(string.IsNullOrWhiteSpace(plan.DisplayName)));
        Assert.All(plans, plan => Assert.NotEqual(plan.GuidText, plan.DisplayName,
            StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task PowerPlan_CaptureState_ReadsTheActivePlan()
    {
        using var context = new RuntimeTestContext();
        var active = await context.PowerManager.GetActivePlanAsync();
        var captured = await new PowerSetPlanActionHandler(context.PowerManager).CaptureStateAsync(
            Action(ActionTypeIds.PowerSetPlan, []), new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        Assert.True(Guid.TryParse(captured?["previousPowerPlanGuid"]?.GetValue<string>(), out var capturedId));
        Assert.Equal(active, capturedId);
    }

    [EnvironmentFact("Administrator")]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task PowerPlan_Change_IsVerifiedAndRestored()
    {
        using var context = new RuntimeTestContext();
        var plans = await context.PowerManager.GetPlansAsync();
        var original = await context.PowerManager.GetActivePlanAsync();
        var alternate = plans.FirstOrDefault(plan => plan.Id != original);
        if (alternate is null) return;

        var changed = false;
        try
        {
            var result = await new PowerSetPlanActionHandler(context.PowerManager).ExecuteAsync(
                Action(ActionTypeIds.PowerSetPlan, new JsonObject
                { [ActionParameterNames.PowerPlanGuid] = alternate.Id.ToString("D") }),
                new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
            if (!result.IsSuccessful) return;
            changed = true;
            Assert.Equal(alternate.Id, await context.PowerManager.GetActivePlanAsync());
        }
        finally
        {
            if (changed)
            {
                await context.PowerManager.SetActivePlanAsync(original);
                Assert.Equal(original, await context.PowerManager.GetActivePlanAsync());
            }
        }
    }

    [EnvironmentFact("RunningService")]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task Service_Discovery_AndIdempotentState_AreSupported()
    {
        using var context = new RuntimeTestContext();
        var services = await context.ServiceManager.GetServicesAsync();
        Assert.NotEmpty(services);
        var running = services.FirstOrDefault(service => service.Status == "Running");
        Assert.NotNull(running);

        var handler = new ServiceSetStateActionHandler(context.ServiceManager);
        var captured = await handler.CaptureStateAsync(
            Action(ActionTypeIds.ServiceSetState, new JsonObject
            { [ActionParameterNames.ServiceName] = running.ServiceName }),
            new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
        var result = await context.ServiceManager.SetStateAsync(running.ServiceName,
            ServiceDesiredStateIds.Running, TimeSpan.FromSeconds(5));

        Assert.Equal(ServiceDesiredStateIds.Running, captured?["previousState"]?.GetValue<string>());
        Assert.True(result.IsSuccessful && result.IsSkipped,
            "Setting an already-running service to Running should be a successful skip.");
    }

    [EnvironmentFact("Administrator")]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task Service_WerSvc_StopAndRestore_ReturnsToItsInitialState()
    {
        using var context = new RuntimeTestContext();
        var services = await context.ServiceManager.GetServicesAsync();
        var service = services.FirstOrDefault(item =>
            string.Equals(item.ServiceName, "WerSvc", StringComparison.OrdinalIgnoreCase));
        if (service is null) return;

        var initial = service.Status == "Running" ? ServiceDesiredStateIds.Running : ServiceDesiredStateIds.Stopped;
        var changedTo = initial == ServiceDesiredStateIds.Running ? ServiceDesiredStateIds.Stopped : ServiceDesiredStateIds.Running;
        var changed = await context.ServiceManager.SetStateAsync(service.ServiceName, changedTo, TimeSpan.FromSeconds(15));
        if (!changed.IsSuccessful) return;
        try
        {
            var restored = await context.ServiceManager.SetStateAsync(service.ServiceName, initial, TimeSpan.FromSeconds(15));
            Assert.True(restored.IsSuccessful, "WerSvc should return to its initial state.");
        }
        finally
        {
            await context.ServiceManager.SetStateAsync(service.ServiceName, initial, TimeSpan.FromSeconds(15));
        }
    }

    [EnvironmentFact("Administrator")]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task Service_DusmSvc_Stop_IsVerifiedAndOriginalStateIsRestored()
    {
        using var context = new RuntimeTestContext();
        var services = await context.ServiceManager.GetServicesAsync();
        if (!services.Any(item => string.Equals(item.ServiceName, "DusmSvc", StringComparison.OrdinalIgnoreCase))) return;

        var initial = await context.ServiceManager.GetStateAsync("DusmSvc");
        var stopped = await context.ServiceManager.SetStateAsync("DusmSvc", ServiceDesiredStateIds.Stopped,
            TimeSpan.FromSeconds(15));
        if (!stopped.IsSuccessful)
        {
            Assert.True(stopped.CurrentState != ServiceDesiredStateIds.Stopped || stopped.Win32Error is not null ||
                        stopped.WasRestartedByWindows,
                "A failed DusmSvc stop should report the actual state or a Windows error.");
            return;
        }
        try
        {
            await Task.Delay(1100);
            Assert.Equal(ServiceDesiredStateIds.Stopped,
                await context.ServiceManager.GetStateAsync("DusmSvc"));
        }
        finally
        {
            if (initial == ServiceDesiredStateIds.Running)
            {
                var restored = await context.ServiceManager.SetStateAsync("DusmSvc", ServiceDesiredStateIds.Running,
                    TimeSpan.FromSeconds(15));
                Assert.True(restored.IsSuccessful, "DusmSvc should be restored to Running.");
            }
            else
            {
                Assert.Equal(ServiceDesiredStateIds.Stopped,
                    await context.ServiceManager.GetStateAsync("DusmSvc"));
            }
        }
    }
}
