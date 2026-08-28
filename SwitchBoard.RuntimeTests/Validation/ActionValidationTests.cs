using SwitchBoard.RuntimeTests.TestInfrastructure;

namespace SwitchBoard.RuntimeTests.Validation;

[Collection("Windows runtime")]
public sealed class ActionValidationTests : RuntimeTestBase
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ActionOptions_RoundTripWithoutPersistingUiState()
    {
        using var context = new RuntimeTestContext();
        var localization = new TestLocalizationService();
        var action = new ActionItemViewModel(new ActionDefinition
        {
            Type = ActionTypeIds.ServiceSetState, Timeout = TimeSpan.FromSeconds(17),
            FailurePolicy = ActionFailurePolicy.Stop, RestoreBehavior = ActionRestoreBehavior.RestorePreviousState,
            Parameters = new JsonObject { [ActionParameterNames.DesiredState] = ServiceDesiredStateIds.Unchanged }
        }, localization);
        var json = JsonSerializer.Serialize(action.ToModel());
        var reloaded = JsonSerializer.Deserialize<ActionDefinition>(json);

        Assert.False(action.IsAdvancedOptionsExpanded);
        Assert.Equal(TimeSpan.FromSeconds(17), reloaded?.Timeout);
        Assert.Equal(ActionFailurePolicy.Stop, reloaded?.FailurePolicy);
        Assert.Equal(ActionRestoreBehavior.RestorePreviousState, reloaded?.RestoreBehavior);
        Assert.DoesNotContain("AdvancedOptions", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ActionOptions_CollapseHidesAdvancedSectionWithoutResettingValues()
    {
        var action = new ActionItemViewModel(new ActionDefinition
        {
            Type = ActionTypeIds.ServiceSetState, Timeout = TimeSpan.FromSeconds(17),
            FailurePolicy = ActionFailurePolicy.Stop,
            Parameters = new JsonObject { [ActionParameterNames.DesiredState] = ServiceDesiredStateIds.Unchanged }
        }, new TestLocalizationService());
        action.IsExpanded = true;
        action.IsAdvancedOptionsExpanded = true;
        action.IsExpanded = false;

        Assert.False(action.IsAdvancedOptionsExpanded);
        Assert.Equal(17, action.TimeoutSeconds);
        Assert.Equal("stop", action.FailurePolicyId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ActionValidation_InvalidConfigurationsAreRejected()
    {
        var localization = new TestLocalizationService();
        var invalidProgram = new ActionItemViewModel(Action(ActionTypeIds.ProgramRun, []), localization);
        var delay = new ActionItemViewModel(Action(ActionTypeIds.Delay,
            new JsonObject { [ActionParameterNames.DelaySeconds] = 2 }), localization);
        var invalidRestoreScript = new ActionItemViewModel(Action(ActionTypeIds.ScriptRun,
            new JsonObject { [ActionParameterNames.ScriptPath] = "missing.ps1" }), localization)
        { RestoreBehaviorId = "restoreScript" };

        Assert.False(invalidProgram.IsValid);
        Assert.False(invalidProgram.SupportsRestore);
        Assert.False(delay.SupportsRestore);
        Assert.False(invalidRestoreScript.IsValid);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ProcessConfigure_NoParametersSelected_IsNoOp()
    {
        var localization = new TestLocalizationService();
        var action = new ActionItemViewModel(Action(ActionTypeIds.ProcessConfigure,
            new JsonObject { [ActionParameterNames.ProcessName] = "example" }), localization);

        Assert.False(action.IsValid);
        Assert.Equal(localization.GetString("Validation.NoOp"), action.ValidationMessage);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ProcessConfigure_RestoreChoicesFollowTheSelectedOperation()
    {
        var localization = new TestLocalizationService();
        var configure = Action(ActionTypeIds.ProcessConfigure, new JsonObject
        {
            [ActionParameterNames.ProcessName] = "example",
            [ActionParameterNames.ProcessOperation] = ProcessOperationIds.Configure
        });
        configure.RestoreBehavior = ActionRestoreBehavior.RestartIfWasRunning;
        var stop = Action(ActionTypeIds.ProcessConfigure, new JsonObject
        {
            [ActionParameterNames.ProcessName] = "example",
            [ActionParameterNames.ProcessOperation] = ProcessOperationIds.Stop
        });
        stop.RestoreBehavior = ActionRestoreBehavior.RestorePreviousState;

        var configureEditor = new ActionItemViewModel(configure, localization);
        var stopEditor = new ActionItemViewModel(stop, localization);

        Assert.Equal(new[] { "none", "previous" }, configureEditor.AvailableRestoreBehaviors.Select(item => item.Value));
        Assert.Equal("none", configureEditor.RestoreBehaviorId);
        Assert.Equal(new[] { "none", "restart" }, stopEditor.AvailableRestoreBehaviors.Select(item => item.Value));
        Assert.Equal("none", stopEditor.RestoreBehaviorId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ProcessConfigure_ChangingOperationResetsIncompatibleRestore()
    {
        var editor = new ActionItemViewModel(Action(ActionTypeIds.ProcessConfigure, new JsonObject
        {
            [ActionParameterNames.ProcessName] = "example",
            [ActionParameterNames.ProcessOperation] = ProcessOperationIds.Configure
        }), new TestLocalizationService());
        editor.RestoreBehaviorId = "previous";
        editor.ProcessOperation = ProcessOperationIds.Stop;
        Assert.Equal("none", editor.RestoreBehaviorId);
        editor.RestoreBehaviorId = "restart";
        editor.ProcessOperation = ProcessOperationIds.Configure;

        Assert.Equal("none", editor.RestoreBehaviorId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ServiceValidation_RecognizesStateOnlyStartupOnlyAndNoOp()
    {
        var localization = new TestLocalizationService();
        var stateOnly = CreateServiceAction(ServiceDesiredStateIds.Unchanged, ServiceStartupTypeIds.Manual);
        var startupOnly = CreateServiceAction(ServiceDesiredStateIds.Running, ServiceStartupTypeIds.Unchanged);
        var noOp = CreateServiceAction(ServiceDesiredStateIds.Unchanged, ServiceStartupTypeIds.Unchanged);
        var stateEditor = new ActionItemViewModel(stateOnly, localization);
        var startupEditor = new ActionItemViewModel(startupOnly, localization);
        var noOpEditor = new ActionItemViewModel(noOp, localization);

        Assert.True(stateEditor.IsValid);
        Assert.True(startupEditor.IsValid);
        Assert.False(noOpEditor.IsValid);
        Assert.Contains("ServiceStartupType.Manual", stateEditor.Summary, StringComparison.Ordinal);
        Assert.Equal(localization.GetString("Validation.NoOp"), noOpEditor.ValidationMessage);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task ServiceHandler_StartupOnlyChange_IsAppliedWithoutStateChange()
    {
        using var context = new RuntimeTestContext();
        var action = CreateServiceAction(ServiceDesiredStateIds.Unchanged, ServiceStartupTypeIds.Manual);
        var noOp = CreateServiceAction(ServiceDesiredStateIds.Unchanged, ServiceStartupTypeIds.Unchanged);
        var manager = new TestServiceManager(ServiceDesiredStateIds.Running, true, ServiceStartupTypeIds.Automatic);
        var handler = new ServiceSetStateActionHandler(manager);

        var changed = await handler.ExecuteAsync(action, new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
        var skipped = await handler.ExecuteAsync(noOp, new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        Assert.True(changed.IsSuccessful && !changed.IsSkipped);
        Assert.True(skipped.IsSuccessful && skipped.IsSkipped);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ProcessConfigure_NewActionsDefaultToNoChange()
    {
        var editor = new ActionItemViewModel(Action(ActionTypeIds.ProcessConfigure,
            new JsonObject { [ActionParameterNames.ProcessName] = "example" }), new TestLocalizationService());
        var model = editor.ToModel();

        Assert.Equal(ProcessPriorityIds.NoChange, editor.ProcessPriority);
        Assert.Equal(ProcessMemoryPriorityIds.NoChange, editor.ProcessMemoryPriority);
        Assert.False(model.Parameters[ActionParameterNames.ChangePriority]?.GetValue<bool>());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ProcessConfigure_LegacyPriorityFlagsLoadCompatibly()
    {
        var localization = new TestLocalizationService();
        var legacyNoPriority = new ActionItemViewModel(Action(ActionTypeIds.ProcessConfigure, new JsonObject
        {
            [ActionParameterNames.ProcessName] = "example",
            [ActionParameterNames.ChangePriority] = false,
            [ActionParameterNames.ProcessPriority] = ProcessPriorityIds.High
        }), localization);
        var explicitPriority = new ActionItemViewModel(Action(ActionTypeIds.ProcessConfigure, new JsonObject
        {
            [ActionParameterNames.ProcessName] = "example",
            [ActionParameterNames.ChangePriority] = true,
            [ActionParameterNames.ProcessPriority] = ProcessPriorityIds.High
        }), localization);

        Assert.Equal(ProcessPriorityIds.NoChange, legacyNoPriority.ProcessPriority);
        Assert.Equal(ProcessPriorityIds.High, explicitPriority.ProcessPriority);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ProcessConfigure_MemoryPriorityOnly_IsARealSetting()
    {
        var editor = new ActionItemViewModel(Action(ActionTypeIds.ProcessConfigure, new JsonObject
        {
            [ActionParameterNames.ProcessName] = "example",
            [ActionParameterNames.ProcessMemoryPriority] = ProcessMemoryPriorityIds.Low
        }), new TestLocalizationService());

        Assert.True(editor.IsValid);
        Assert.Equal(ProcessMemoryPriorityIds.Low,
            editor.ToModel().Parameters[ActionParameterNames.ProcessMemoryPriority]?.GetValue<string>());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ProcessConfigure_PerformanceModeOnly_IsARealSetting()
    {
        var editor = new ActionItemViewModel(Action(ActionTypeIds.ProcessConfigure, new JsonObject
        {
            [ActionParameterNames.ProcessName] = "example",
            [ActionParameterNames.ProcessPerformanceMode] = ProcessPerformanceModeIds.Efficiency
        }), new TestLocalizationService());

        Assert.True(editor.IsValid);
        Assert.True(editor.ShouldChangePerformanceMode);
        Assert.Equal(ProcessPerformanceModeIds.Efficiency,
            editor.ToModel().Parameters[ActionParameterNames.ProcessPerformanceMode]?.GetValue<string>());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ProcessSettings_NoChange_DoesNotCallASetter()
    {
        var service = new ProcessSettingsService();

        Assert.Throws<InvalidOperationException>(() => service.Apply(Process.GetCurrentProcess(), new JsonObject
        {
            [ActionParameterNames.ChangePriority] = false,
            [ActionParameterNames.ProcessPriority] = ProcessPriorityIds.NoChange,
            [ActionParameterNames.ProcessMemoryPriority] = ProcessMemoryPriorityIds.NoChange,
            [ActionParameterNames.ProcessPerformanceMode] = ProcessPerformanceModeIds.NoChange
        }));
    }

    [EnvironmentFact("PowerQoS")]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public void ProcessSettings_PerformanceMode_CapturesAndRestoresActualMasks()
    {
        var service = new ProcessSettingsService();
        var parameters = new JsonObject
        { [ActionParameterNames.ProcessPerformanceMode] = ProcessPerformanceModeIds.HighPerformance };
        using var process = Process.GetCurrentProcess();
        var original = service.Capture(process, parameters);
        try
        {
            service.Apply(process, new JsonObject
            { [ActionParameterNames.ProcessPerformanceMode] = ProcessPerformanceModeIds.WindowsDefault });
            var windowsDefault = service.Capture(process, parameters);
            service.Apply(process, new JsonObject
            { [ActionParameterNames.ProcessPerformanceMode] = ProcessPerformanceModeIds.HighPerformance });
            var high = service.Capture(process, parameters);
            service.Apply(process, new JsonObject
            { [ActionParameterNames.ProcessPerformanceMode] = ProcessPerformanceModeIds.Efficiency });
            var efficiency = service.Capture(process, parameters);

            Assert.Equal(0u, windowsDefault["performanceControlMask"]?.GetValue<uint>());
            Assert.Equal(0u, windowsDefault["performanceStateMask"]?.GetValue<uint>());
            Assert.Equal((1u, 0u), ProcessSettingsService.PerformanceMasksFor(ProcessPerformanceModeIds.HighPerformance));
            Assert.Contains("performanceControlMask", high);
            Assert.Equal((1u, 1u), ProcessSettingsService.PerformanceMasksFor(ProcessPerformanceModeIds.Efficiency));
            Assert.Contains("performanceControlMask", efficiency);
            service.Restore(process, original);
            var restored = service.Capture(process, parameters);
            Assert.Equal(original["performanceControlMask"]?.GetValue<uint>(), restored["performanceControlMask"]?.GetValue<uint>());
            Assert.Equal(original["performanceStateMask"]?.GetValue<uint>(), restored["performanceStateMask"]?.GetValue<uint>());
        }
        finally
        {
            try { service.Restore(process, original); } catch { }
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void LegacyProcessSetState_LoadsAsProcessConfigureStop()
    {
        var editor = new ActionItemViewModel(Action(ActionTypeIds.ProcessSetState, new JsonObject
        {
            [ActionParameterNames.ProcessName] = "legacy-app",
            [ActionParameterNames.DesiredState] = ProcessDesiredStateIds.Stopped
        }), new TestLocalizationService());
        var model = editor.ToModel();

        Assert.Equal(ActionTypeIds.ProcessConfigure, editor.Type);
        Assert.True(editor.IsProcessStopMode);
        Assert.Equal(ActionTypeIds.ProcessConfigure, model.Type);
        Assert.Equal(ProcessOperationIds.Stop,
            model.Parameters[ActionParameterNames.ProcessOperation]?.GetValue<string>());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ProcessConfigure_OperationAndAffinityRoundTripPreserveAllCpuMeaning()
    {
        var model = Action(ActionTypeIds.ProcessConfigure, new JsonObject
        {
            [ActionParameterNames.ProcessName] = "example",
            [ActionParameterNames.ProcessOperation] = ProcessOperationIds.Configure,
            [ActionParameterNames.ChangeAffinity] = true,
            [ActionParameterNames.CpuIndices] = new JsonArray(0, 1)
        });
        var roundTrip = JsonSerializer.Deserialize<ActionDefinition>(JsonSerializer.Serialize(model));

        Assert.Equal(ProcessOperationIds.Configure,
            roundTrip?.Parameters[ActionParameterNames.ProcessOperation]?.GetValue<string>());
        Assert.Equal(0UL, ProcessConfigureActionHandler.ReadAffinityMask(null));
        Assert.NotEqual(0UL, ProcessConfigureActionHandler.ReadAffinityMask(
            model.Parameters[ActionParameterNames.CpuIndices] as JsonArray));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ProcessConfigure_ChangingOperationHidesSettingsWithoutDiscardingThem()
    {
        var editor = new ActionItemViewModel(Action(ActionTypeIds.ProcessConfigure, new JsonObject
        {
            [ActionParameterNames.ProcessName] = "example",
            [ActionParameterNames.ProcessOperation] = ProcessOperationIds.Configure,
            [ActionParameterNames.ChangeAffinity] = true,
            [ActionParameterNames.ChangePriority] = true,
            [ActionParameterNames.CpuIndices] = new JsonArray(1),
            [ActionParameterNames.ProcessPriority] = ProcessPriorityIds.High
        }), new TestLocalizationService());
        editor.ProcessOperation = ProcessOperationIds.Stop;
        var stopped = editor.ToModel();
        editor.ProcessOperation = ProcessOperationIds.Configure;

        Assert.False(editor.IsProcessStopMode);
        Assert.True(editor.IsProcessConfigureOperation);
        Assert.Equal(ProcessOperationIds.Stop, stopped.Parameters[ActionParameterNames.ProcessOperation]?.GetValue<string>());
        Assert.True(stopped.Parameters[ActionParameterNames.ChangeAffinity]?.GetValue<bool>());
        Assert.True(stopped.Parameters[ActionParameterNames.ChangePriority]?.GetValue<bool>());
        Assert.Equal(ProcessPriorityIds.High, stopped.Parameters[ActionParameterNames.ProcessPriority]?.GetValue<string>());
        Assert.Equal(2UL, ProcessConfigureActionHandler.ReadAffinityMask(
            stopped.Parameters[ActionParameterNames.CpuIndices] as JsonArray));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ProcessLiveStatus_IsHiddenUntilConfiguredAndVisibleForRuntimeResults()
    {
        var localization = new TestLocalizationService();
        var unconfigured = new ActionItemViewModel(Action(ActionTypeIds.ProcessConfigure, []), localization);
        unconfigured.SetCurrentStatus(null, "No process name is configured.", DateTimeOffset.Now);
        var configured = new ActionItemViewModel(Action(ActionTypeIds.ProcessConfigure, new JsonObject
        {
            [ActionParameterNames.ProcessName] = "example",
            [ActionParameterNames.ProcessOperation] = ProcessOperationIds.Stop
        }), localization);
        configured.SetCurrentStatus("Running", "ProcessName=example; MatchingProcesses=1", DateTimeOffset.Now);
        var unavailable = new ActionItemViewModel(Action(ActionTypeIds.ProcessConfigure, new JsonObject
        {
            [ActionParameterNames.ProcessName] = "example",
            [ActionParameterNames.ProcessOperation] = ProcessOperationIds.Stop
        }), localization);
        unavailable.SetCurrentStatus(null, "Access denied.", DateTimeOffset.Now);

        Assert.False(unconfigured.ShouldMonitorCurrentStatus);
        Assert.False(unconfigured.ShouldShowCurrentStatus);
        Assert.True(string.IsNullOrWhiteSpace(unconfigured.CurrentStatusText));
        Assert.True(configured.ShouldMonitorCurrentStatus && configured.ShouldShowCurrentStatus);
        Assert.True(unavailable.ShouldMonitorCurrentStatus && unavailable.ShouldShowCurrentStatus);
        Assert.Equal(localization.GetString("ActionStatus.Unavailable"), unavailable.CurrentStatusText);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Condition_NestedActions_PreserveTypesAndOrderAfterSerialization()
    {
        var model = Action(ActionTypeIds.ConditionIf, new JsonObject
        {
            [ActionParameterNames.ConditionType] = ConditionTypeIds.ProcessRunning,
            [ActionParameterNames.ConditionValue] = "notepad",
            [ActionParameterNames.ThenActions] = new JsonArray(JsonSerializer.SerializeToNode(
                Action(ActionTypeIds.Delay, new JsonObject { [ActionParameterNames.DelaySeconds] = 1 }))!),
            [ActionParameterNames.ElseActions] = new JsonArray(JsonSerializer.SerializeToNode(
                Action(ActionTypeIds.NotificationShow, new JsonObject
                {
                    [ActionParameterNames.NotificationMessage] = "Condition branch",
                    [ActionParameterNames.NotificationLevel] = NotificationLevelIds.Info
                }))!)
        });
        var editor = new ActionItemViewModel(model, new TestLocalizationService());
        var roundTrip = new ActionItemViewModel(
            JsonSerializer.Deserialize<ActionDefinition>(JsonSerializer.Serialize(editor.ToModel()))!,
            new TestLocalizationService());

        Assert.Equal(ActionTypeIds.ConditionIf, editor.Type);
        Assert.Single(editor.ThenActions);
        Assert.Single(editor.ElseActions);
        Assert.Equal(ActionTypeIds.Delay, editor.ThenActions[0].Type);
        Assert.Equal(ActionTypeIds.NotificationShow, editor.ElseActions[0].Type);
        Assert.Equal(0, roundTrip.ThenActions[0].SortOrder);
        Assert.Equal(0, roundTrip.ElseActions[0].SortOrder);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Condition_IncompleteNestedAction_MakesParentInvalid()
    {
        var editor = new ActionItemViewModel(Action(ActionTypeIds.ConditionIf, new JsonObject
        {
            [ActionParameterNames.ConditionType] = ConditionTypeIds.ProcessRunning,
            [ActionParameterNames.ConditionValue] = "notepad",
            [ActionParameterNames.ThenActions] = new JsonArray(JsonSerializer.SerializeToNode(
                Action(ActionTypeIds.ProgramRun, []))!)
        }), new TestLocalizationService());

        Assert.False(editor.IsValid);
        Assert.Equal(new TestLocalizationService().GetString("Validation.NestedAction"), editor.ValidationMessage);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Condition_BranchesAddActionsThroughTheSharedActionModel()
    {
        var editor = new ActionItemViewModel(Action(ActionTypeIds.ConditionIf, new JsonObject
        {
            [ActionParameterNames.ConditionType] = ConditionTypeIds.ProcessRunning,
            [ActionParameterNames.ConditionValue] = "notepad"
        }), new TestLocalizationService());
        var thenAction = editor.AddNestedAction(ActionTypeIds.Delay,
            new JsonObject { [ActionParameterNames.DelaySeconds] = 1 }, true);
        var elseAction = editor.AddNestedAction(ActionTypeIds.NotificationShow, new JsonObject
        {
            [ActionParameterNames.NotificationMessage] = "Else",
            [ActionParameterNames.NotificationLevel] = NotificationLevelIds.Info
        }, false);

        Assert.Equal(ActionTypeIds.Delay, thenAction?.Type);
        Assert.Equal(ActionTypeIds.NotificationShow, elseAction?.Type);
        Assert.Single(editor.ThenActions);
        Assert.Single(editor.ElseActions);
    }

    private static ActionDefinition CreateServiceAction(string state, string startupType) =>
        Action(ActionTypeIds.ServiceSetState, new JsonObject
        {
            [ActionParameterNames.ServiceName] = "Example",
            [ActionParameterNames.DesiredState] = state,
            [ActionParameterNames.ServiceStartupType] = startupType
        });
}
