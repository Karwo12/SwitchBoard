using SwitchBoard.RuntimeTests.TestInfrastructure;
using SwitchBoard.Services.Actions;
using SwitchBoard.Views;
using System.Text;

namespace SwitchBoard.RuntimeTests.ViewModels;

public sealed class ActionEditingRegressionTests : RuntimeTestBase
{
    [Fact]
    [Trait("Category", "Unit")]
    public void EveryPickerAction_CanBeCreatedSummarizedAndRoundTripped()
    {
        var localization = new TestLocalizationService();
        var descriptors = ActionDescriptorRegistry.PickerDescriptors.ToArray();

        Assert.Equal(ActionTypeIds.All.Count - 1, descriptors.Length);
        Assert.Equal(descriptors.Length, descriptors.Select(item => item.TypeId)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var descriptor in descriptors)
        {
            var editor = new ActionItemViewModel(new ActionDefinition
            {
                Type = descriptor.TypeId,
                Parameters = descriptor.CreateDefaultParameters(nested: false)
            }, localization);

            Assert.Equal(descriptor.TypeId, editor.Type);
            Assert.NotNull(editor.Summary);
            Assert.NotNull(editor.ValidationMessage);

            var reopened = new ActionItemViewModel(editor.ToModel(), localization);
            Assert.Equal(descriptor.TypeId, reopened.Type);
            Assert.Equal(editor.Summary, reopened.Summary);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void DisplayCandidate_KeepsTechnicalIdSeparateFromFriendlyMonitorName()
    {
        var candidate = new DisplayCandidate("\\\\.\\DISPLAY1", "\\\\?\\DISPLAY#MONITOR-123",
            "ASUS PG27AQDP", 1, 2560, 1440, 480, true,
            [new DisplayModeCandidate(2560, 1440, 480, 32)]);

        Assert.NotEqual(candidate.DeviceId, candidate.DisplayName);
        Assert.Equal("ASUS PG27AQDP", candidate.DisplayName);
        Assert.Equal("\\\\?\\DISPLAY#MONITOR-123", candidate.DeviceId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void MonitorNameResolver_DoesNotStopAtGenericDisplayConfigName()
    {
        var edid = new byte[128];
        edid[54] = 0;
        edid[55] = 0;
        edid[56] = 0;
        edid[57] = 0xFC;
        Encoding.ASCII.GetBytes("ASUS PG27AQDP").CopyTo(edid, 59);

        var resolution = MonitorNameResolver.Resolve(
            "Generic PnP Monitor", "Generic PnP Monitor", "Generic Monitor",
            MonitorNameResolver.ExtractEdidProductName(edid));

        Assert.Equal("ASUS PG27AQDP", resolution.DisplayName);
        Assert.Equal("EdidProductName", resolution.Source);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void MonitorNameResolver_PrefersRealFriendlyNameAndKeepsTechnicalIdIndependent()
    {
        var candidate = new DisplayCandidate("\\\\.\\DISPLAY1", "MONITOR-TECHNICAL-ID",
            MonitorNameResolver.Resolve("ASUS PG27AQDP", "Generic PnP Monitor", "Generic Monitor", null).DisplayName,
            1, 2560, 1440, 200, true, []);

        Assert.Equal("ASUS PG27AQDP", candidate.DisplayName);
        Assert.Equal("MONITOR-TECHNICAL-ID", candidate.DeviceId);
        Assert.NotEqual(candidate.DeviceId, candidate.DisplayName);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void DisplayEditor_UsesFriendlyNameAndOnlyAllowsRatesForSelectedResolution()
    {
        var editor = new ActionItemViewModel(Action(ActionTypeIds.DisplayConfigure, []),
            new TestLocalizationService());
        var candidate = new DisplayCandidate("DISPLAY1", "MONITOR-ID", "ASUS PG27AQDP", 1,
            1920, 1080, 60, true,
            [
                new DisplayModeCandidate(1920, 1080, 60, 32),
                new DisplayModeCandidate(1920, 1080, 144, 32),
                new DisplayModeCandidate(2560, 1440, 240, 32),
                new DisplayModeCandidate(2560, 1440, 480, 32)
            ]);

        editor.ApplyDisplayCandidate(candidate);
        editor.SelectedDisplayResolution = editor.AvailableDisplayResolutions.Single(item =>
            item.Width == 2560 && item.Height == 1440);

        Assert.Equal("ASUS PG27AQDP", editor.DisplayMonitorName);
        Assert.Equal("ASUS PG27AQDP", editor.Name);
        Assert.Equal(new[] { 240, 480 }, editor.AvailableDisplayRefreshRates);
        editor.DisplayRefreshRate = 480;
        editor.DisplayRefreshRate = 144;
        Assert.Equal(480, editor.DisplayRefreshRate);
        Assert.Contains("ASUS PG27AQDP", editor.Summary, StringComparison.Ordinal);
        Assert.Contains("480 Hz", editor.Summary, StringComparison.Ordinal);

        var serialized = editor.ToModel();
        Assert.Equal(480, serialized.Parameters[ActionParameterNames.DisplayRefreshRate]?.GetValue<int>());
        var roundTrip = new ActionItemViewModel(serialized, new TestLocalizationService());
        Assert.Equal(480, roundTrip.DisplayRefreshRate);
        Assert.Equal("ASUS PG27AQDP", roundTrip.DisplayMonitorName);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void DisplaySkipConfirmation_DefaultsToFalseAndRoundTrips()
    {
        var editor = new ActionItemViewModel(Action(ActionTypeIds.DisplayConfigure, []),
            new TestLocalizationService());

        Assert.False(editor.SkipDisplayConfirmation);
        editor.SkipDisplayConfirmation = true;

        var roundTrip = new ActionItemViewModel(editor.ToModel(), new TestLocalizationService());
        Assert.True(roundTrip.SkipDisplayConfirmation);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void DisplayCustomRestore_UsesDiscoveredModesAndRoundTrips()
    {
        var candidate = new DisplayCandidate("DISPLAY1", "MONITOR-ID", "ASUS PG27AQDP", 1,
            1920, 1080, 60, true,
            [
                new DisplayModeCandidate(1920, 1080, 60, 32),
                new DisplayModeCandidate(1920, 1080, 120, 32),
                new DisplayModeCandidate(2560, 1440, 165, 32),
                new DisplayModeCandidate(2560, 1440, 200, 32)
            ]);
        var editor = new ActionItemViewModel(Action(ActionTypeIds.DisplayConfigure, new JsonObject
        {
            [ActionParameterNames.DisplayDeviceName] = "DISPLAY1",
            [ActionParameterNames.DisplayDeviceId] = "MONITOR-ID",
            [ActionParameterNames.DisplayWidth] = 1920,
            [ActionParameterNames.DisplayHeight] = 1080,
            [ActionParameterNames.DisplayRefreshRate] = 60,
            [ActionParameterNames.RestoreDisplayDeviceName] = "DISPLAY1",
            [ActionParameterNames.RestoreDisplayDeviceId] = "MONITOR-ID",
            [ActionParameterNames.RestoreDisplayName] = "ASUS PG27AQDP",
            [ActionParameterNames.RestoreDisplayWidth] = 2560,
            [ActionParameterNames.RestoreDisplayHeight] = 1440,
            [ActionParameterNames.RestoreDisplayRefreshRate] = 165
        }), new TestLocalizationService());
        editor.RestoreBehaviorId = "custom";

        editor.ApplyAvailableRestoreDisplays([candidate]);

        Assert.Same(candidate, editor.SelectedRestoreDisplay);
        Assert.Equal(new[] { 165, 200 }, editor.AvailableRestoreDisplayRefreshRates);
        Assert.True(editor.IsValid);
        editor.SelectedRestoreDisplayResolution = editor.AvailableRestoreDisplayResolutions.Single(item =>
            item.Width == 1920 && item.Height == 1080);
        Assert.Equal(new[] { 60, 120 }, editor.AvailableRestoreDisplayRefreshRates);
        editor.RestoreDisplayRefreshRate = 200;
        Assert.Equal(60, editor.RestoreDisplayRefreshRate);

        var serialized = editor.ToModel();
        Assert.Equal(ActionRestoreBehavior.RestoreCustomState, serialized.RestoreBehavior);
        Assert.Equal(1920, serialized.Parameters[ActionParameterNames.RestoreDisplayWidth]?.GetValue<int>());
        var roundTrip = new ActionItemViewModel(serialized, new TestLocalizationService());
        Assert.Equal("custom", roundTrip.RestoreBehaviorId);
        Assert.Equal(60, roundTrip.RestoreDisplayRefreshRate);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void DisplayCustomRestore_DoesNotSelectAReplacementForMissingMonitorOrMode()
    {
        var editor = new ActionItemViewModel(Action(ActionTypeIds.DisplayConfigure, new JsonObject
        {
            [ActionParameterNames.DisplayDeviceName] = "DISPLAY1",
            [ActionParameterNames.DisplayDeviceId] = "MONITOR-ID",
            [ActionParameterNames.DisplayWidth] = 1920,
            [ActionParameterNames.DisplayHeight] = 1080,
            [ActionParameterNames.DisplayRefreshRate] = 60,
            [ActionParameterNames.RestoreDisplayDeviceName] = "DISPLAY1",
            [ActionParameterNames.RestoreDisplayDeviceId] = "MONITOR-ID",
            [ActionParameterNames.RestoreDisplayWidth] = 3440,
            [ActionParameterNames.RestoreDisplayHeight] = 1440,
            [ActionParameterNames.RestoreDisplayRefreshRate] = 165
        }), new TestLocalizationService());
        editor.RestoreBehaviorId = "custom";

        editor.ApplyAvailableRestoreDisplays([
            new DisplayCandidate("DISPLAY2", "OTHER-MONITOR", "Other monitor", 2,
                1920, 1080, 60, false, [new DisplayModeCandidate(1920, 1080, 60, 32)])
        ]);

        Assert.Null(editor.SelectedRestoreDisplay);
        Assert.False(editor.IsValid);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ProgramTargetType_UsesSafeLegacyInferenceAndSerializesExplicitly()
    {
        var legacyExe = new ActionItemViewModel(Action(ActionTypeIds.ProgramRun, new JsonObject
        {
            [ActionParameterNames.Target] = @"C:\Apps\Example.exe"
        }), new TestLocalizationService());
        var legacyUri = new ActionItemViewModel(Action(ActionTypeIds.ProgramRun, new JsonObject
        {
            [ActionParameterNames.Target] = "steam://rungameid/730"
        }), new TestLocalizationService());

        Assert.True(legacyExe.IsExecutableTarget);
        Assert.True(legacyUri.IsUriTarget);
        Assert.Equal(TargetTypeIds.Executable,
            legacyExe.ToModel().Parameters[ActionParameterNames.TargetType]?.GetValue<string>());
        Assert.Equal(TargetTypeIds.Uri,
            legacyUri.ToModel().Parameters[ActionParameterNames.TargetType]?.GetValue<string>());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ProgramTargetType_ChangesUiStateWithoutChangingTargetValue()
    {
        var editor = new ActionItemViewModel(Action(ActionTypeIds.ProgramRun, new JsonObject
        {
            [ActionParameterNames.Target] = "steam://rungameid/730",
            [ActionParameterNames.TargetType] = TargetTypeIds.Uri
        }), new TestLocalizationService());

        editor.TargetType = TargetTypeIds.Executable;
        Assert.True(editor.IsExecutableTarget);
        Assert.False(editor.IsUriTarget);
        Assert.Equal("steam://rungameid/730", editor.Target);

        editor.TargetType = TargetTypeIds.Uri;
        Assert.True(editor.IsUriTarget);
        Assert.Equal("steam://rungameid/730", editor.Target);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UriWithoutProcessDependentFeatures_DoesNotRequireAProcess()
    {
        var editor = new ActionItemViewModel(Action(ActionTypeIds.ProgramRun, new JsonObject
        {
            [ActionParameterNames.Target] = "steam://rungameid/730",
            [ActionParameterNames.TargetType] = TargetTypeIds.Uri
        }), new TestLocalizationService());

        Assert.True(editor.IsValid);
        Assert.Equal(string.Empty, editor.ProcessName);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UriRestart_UsesProcessTargetAndOptionalExecutablePath()
    {
        var localization = new TestLocalizationService();
        var valid = new ActionItemViewModel(Action(ActionTypeIds.ProgramRun, new JsonObject
        {
            [ActionParameterNames.Target] = "steam://rungameid/1366800",
            [ActionParameterNames.TargetType] = TargetTypeIds.Uri,
            [ActionParameterNames.ProcessName] = "CrosshairX",
            [ActionParameterNames.ExecutablePath] = @"C:\Games\CrosshairX\CrosshairX.exe",
            [ActionParameterNames.InstanceBehavior] = InstanceBehaviorIds.RestartExisting
        }), localization);

        Assert.True(valid.IsValid);

        var missingProcess = new ActionItemViewModel(Action(ActionTypeIds.ProgramRun, new JsonObject
        {
            [ActionParameterNames.Target] = "steam://rungameid/1366800",
            [ActionParameterNames.TargetType] = TargetTypeIds.Uri,
            [ActionParameterNames.InstanceBehavior] = InstanceBehaviorIds.RestartExisting
        }), localization);

        Assert.False(missingProcess.IsValid);
        Assert.Equal(localization.GetString("Validation.UriRestartProcess"), missingProcess.ValidationMessage);
    }

    [Theory]
    [InlineData(ActionParameterNames.ChangeAffinity)]
    [InlineData(ActionParameterNames.ChangePriority)]
    [InlineData(ActionParameterNames.ProcessMemoryPriority)]
    [InlineData(ActionParameterNames.ProcessPerformanceMode)]
    public void UriWithProcessDependentFeatures_RequiresAProcess(string dependentParameter)
    {
        var parameters = new JsonObject
        {
            [ActionParameterNames.Target] = "steam://rungameid/730",
            [ActionParameterNames.TargetType] = TargetTypeIds.Uri
        };
        if (dependentParameter == ActionParameterNames.ProcessMemoryPriority)
            parameters[dependentParameter] = ProcessMemoryPriorityIds.Low;
        else if (dependentParameter == ActionParameterNames.ProcessPerformanceMode)
            parameters[dependentParameter] = ProcessPerformanceModeIds.HighPerformance;
        else parameters[dependentParameter] = true;

        var editor = new ActionItemViewModel(Action(ActionTypeIds.ProgramRun, parameters),
            new TestLocalizationService());

        Assert.False(editor.IsValid);
        Assert.Equal("Validation.PostLaunchProcess", editor.ValidationMessage);
        editor.ProcessName = "cs2";
        Assert.True(editor.IsValid);
        Assert.Equal("cs2", editor.ToModel().Parameters[ActionParameterNames.ProcessName]?.GetValue<string>());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RestartWindowBehavior_DefaultsToNoChangeAndRoundTrips()
    {
        var editor = new ActionItemViewModel(Action(ActionTypeIds.ProcessConfigure, new JsonObject
        {
            [ActionParameterNames.ProcessName] = "anydesk",
            [ActionParameterNames.ProcessOperation] = ProcessOperationIds.Stop
        }), new TestLocalizationService())
        {
            RestoreBehaviorId = "restart"
        };

        Assert.True(editor.IsRestartWindowBehaviorEnabled);
        Assert.Equal(WindowBehaviorIds.None, editor.WindowBehavior);
        editor.WindowBehavior = WindowBehaviorIds.Minimize;

        var roundTrip = new ActionItemViewModel(editor.ToModel(), new TestLocalizationService());
        Assert.Equal(WindowBehaviorIds.Minimize, roundTrip.WindowBehavior);
        Assert.True(roundTrip.IsRestartWindowBehaviorEnabled);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ArgumentComposer_PreservesManualTextQuotingAndAvoidsDuplicates()
    {
        var localization = new TestLocalizationService();
        var minimized = new ArgumentPresetItem(new ArgumentPreset("minimized", "--start-minimized",
            "description", "compatibility"), localization) { IsSelected = true };
        var profile = new ArgumentPresetItem(new ArgumentPreset("profile", "--profile-directory",
            "description", "compatibility", true, "Profile 1"), localization)
        {
            IsSelected = true,
            Value = "My Profile"
        };

        var merged = SwitchBoard.Views.ArgumentComposer.Merge("--custom \"manual value\" --start-minimized",
            [minimized, profile]);

        Assert.Equal(1, merged.Split("--start-minimized", StringSplitOptions.None).Length - 1);
        Assert.Contains("--custom \"manual value\"", merged, StringComparison.Ordinal);
        Assert.Contains("--profile-directory \"My Profile\"", merged, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ArgumentPresetCatalog_UnknownTargetStillShowsTheGeneralCatalog()
    {
        var presets = ArgumentPresetCatalog.ForTarget("unknown-tool.exe");

        Assert.NotEmpty(presets);
        Assert.All(presets, preset =>
        {
            Assert.False(string.IsNullOrWhiteSpace(preset.DescriptionKey));
            Assert.False(string.IsNullOrWhiteSpace(preset.CompatibilityKey));
        });
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ArgumentPresetCatalog_PutsRecognizedApplicationPresetsFirst()
    {
        var presets = ArgumentPresetCatalog.ForTarget("chrome.exe");

        Assert.NotEmpty(presets);
        Assert.Equal("chromium", presets[0].ApplicationKey);
        Assert.Contains(presets, preset => preset.ApplicationKey == "powershell");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ArgumentPresetFilter_FiltersBySearchAndApplicationWithoutLosingDescriptions()
    {
        var localization = new TestLocalizationService();
        var chrome = new ArgumentPresetItem(new ArgumentPreset(
            "chrome", "--incognito", "description", "Chrome", ApplicationKey: "chromium"), localization);
        var steam = new ArgumentPresetItem(new ArgumentPreset(
            "steam", "-silent", "description", "Steam", ApplicationKey: "steam"), localization);

        Assert.True(ArgumentPresetFilter.Matches(chrome, "incognito", "All", "All"));
        Assert.False(ArgumentPresetFilter.Matches(steam, "incognito", "All", "All"));
        Assert.True(ArgumentPresetFilter.Matches(steam, string.Empty, "Steam", "All"));
        Assert.False(ArgumentPresetFilter.Matches(chrome, string.Empty, "Steam", "All"));
    }
}
