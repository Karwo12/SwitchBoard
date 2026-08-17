using System.Text.Json.Nodes;
using SwitchBoard.Models.Actions;
using SwitchBoard.Services.Windows;

namespace SwitchBoard.Services.Execution.Handlers;

public sealed class AudioConfigureActionHandler(IAudioManager audioManager) : IReversibleActionHandler
{
    public string ActionType => ActionTypeIds.AudioConfigure;

    public async Task<JsonObject?> CaptureStateAsync(ActionDefinition action, ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var state = new JsonObject();
        var output = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.AudioOutputDeviceId);
        var input = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.AudioInputDeviceId);
        var multimedia = ActionParameterReader.ReadBoolean(action.Parameters, ActionParameterNames.SetDefaultMultimedia, true);
        var communications = ActionParameterReader.ReadBoolean(action.Parameters, ActionParameterNames.SetDefaultCommunications, false);
        if (!string.IsNullOrWhiteSpace(output))
        {
            if (multimedia) state["outputMultimedia"] = await audioManager.GetDefaultDeviceIdAsync(false, false, cancellationToken);
            if (communications) state["outputCommunications"] = await audioManager.GetDefaultDeviceIdAsync(false, true, cancellationToken);
        }
        if (!string.IsNullOrWhiteSpace(input))
        {
            if (multimedia) state["inputMultimedia"] = await audioManager.GetDefaultDeviceIdAsync(true, false, cancellationToken);
            if (communications) state["inputCommunications"] = await audioManager.GetDefaultDeviceIdAsync(true, true, cancellationToken);
        }
        if (action.Parameters.ContainsKey(ActionParameterNames.VolumePercent) ||
            action.Parameters.ContainsKey(ActionParameterNames.Mute))
        {
            state["volumeDeviceId"] = await audioManager.GetDefaultDeviceIdAsync(false, false, cancellationToken);
            var current = await audioManager.GetMasterVolumeAsync(null, cancellationToken);
            state["volume"] = current.Volume;
            state["muted"] = current.Muted;
        }
        return state;
    }

    public async Task<ActionExecutionResult> ExecuteAsync(ActionDefinition action, ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var output = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.AudioOutputDeviceId);
        var input = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.AudioInputDeviceId);
        var multimedia = ActionParameterReader.ReadBoolean(action.Parameters, ActionParameterNames.SetDefaultMultimedia, true);
        var communications = ActionParameterReader.ReadBoolean(action.Parameters, ActionParameterNames.SetDefaultCommunications, false);
        if (!string.IsNullOrWhiteSpace(output))
            await audioManager.SetDefaultDeviceAsync(output, multimedia, communications, cancellationToken);
        if (!string.IsNullOrWhiteSpace(input))
            await audioManager.SetDefaultDeviceAsync(input, multimedia, communications, cancellationToken);
        float? volume = action.Parameters.ContainsKey(ActionParameterNames.VolumePercent)
            ? Math.Clamp(ActionParameterReader.ReadInt32(action.Parameters, ActionParameterNames.VolumePercent, 100), 0, 100) / 100f
            : null;
        bool? muted = action.Parameters.ContainsKey(ActionParameterNames.Mute)
            ? ActionParameterReader.ReadBoolean(action.Parameters, ActionParameterNames.Mute, false) : null;
        if (volume is not null || muted is not null)
            await audioManager.SetMasterVolumeAsync(volume, muted, output, cancellationToken);
        if (string.IsNullOrWhiteSpace(output) && string.IsNullOrWhiteSpace(input) && volume is null && muted is null)
            return ActionExecutionResult.Failure("No audio setting was selected.", false);
        var mismatches = await VerifyAsync(output, input, multimedia, communications, volume, muted,
            output, cancellationToken);
        return mismatches.Count == 0
            ? ActionExecutionResult.Success("Verified: the requested audio settings are active.")
            : ActionExecutionResult.Failure("Windows did not apply all requested audio settings: " + string.Join("; ", mismatches));
    }

    public async Task<ActionExecutionResult> RestoreAsync(ActionDefinition action, JsonObject restoreState, ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        foreach (var (name, multimedia, communications) in new[]
                 {
                     ("outputMultimedia", true, false), ("outputCommunications", false, true),
                     ("inputMultimedia", true, false), ("inputCommunications", false, true)
                 })
            if (restoreState[name]?.GetValue<string>() is { Length: > 0 } id)
                await audioManager.SetDefaultDeviceAsync(id, multimedia, communications, cancellationToken);
        if (restoreState["volume"]?.GetValue<float>() is { } volume)
            await audioManager.SetMasterVolumeAsync(volume,
                restoreState["muted"]?.GetValue<bool>(), restoreState["volumeDeviceId"]?.GetValue<string>(), cancellationToken);

        var mismatches = new List<string>();
        foreach (var (key, input, communications) in new[]
                 {
                     ("outputMultimedia", false, false), ("outputCommunications", false, true),
                     ("inputMultimedia", true, false), ("inputCommunications", true, true)
                 })
            if (restoreState[key]?.GetValue<string>() is { Length: > 0 } expected &&
                !string.Equals(await audioManager.GetDefaultDeviceIdAsync(input, communications, cancellationToken),
                    expected, StringComparison.OrdinalIgnoreCase))
                mismatches.Add($"default role {key}");
        if (restoreState["volume"]?.GetValue<float>() is { } expectedVolume)
        {
            var actual = await audioManager.GetMasterVolumeAsync(
                restoreState["volumeDeviceId"]?.GetValue<string>(), cancellationToken);
            if (Math.Abs(actual.Volume - expectedVolume) > 0.011f) mismatches.Add("volume");
            if (restoreState["muted"]?.GetValue<bool>() is { } expectedMute && actual.Muted != expectedMute)
                mismatches.Add("mute");
        }
        return mismatches.Count == 0
            ? ActionExecutionResult.Success("Verified: the previous audio settings are active.")
            : ActionExecutionResult.Failure("Windows did not restore all audio settings: " + string.Join("; ", mismatches));
    }

    private async Task<List<string>> VerifyAsync(string output, string input, bool multimedia, bool communications,
        float? volume, bool? muted, string? volumeDeviceId, CancellationToken cancellationToken)
    {
        var mismatches = new List<string>();
        if (!string.IsNullOrWhiteSpace(output))
        {
            if (multimedia && !string.Equals(await audioManager.GetDefaultDeviceIdAsync(false, false, cancellationToken), output,
                    StringComparison.OrdinalIgnoreCase)) mismatches.Add("default output");
            if (communications && !string.Equals(await audioManager.GetDefaultDeviceIdAsync(false, true, cancellationToken), output,
                    StringComparison.OrdinalIgnoreCase)) mismatches.Add("communications output");
        }
        if (!string.IsNullOrWhiteSpace(input))
        {
            if (multimedia && !string.Equals(await audioManager.GetDefaultDeviceIdAsync(true, false, cancellationToken), input,
                    StringComparison.OrdinalIgnoreCase)) mismatches.Add("default input");
            if (communications && !string.Equals(await audioManager.GetDefaultDeviceIdAsync(true, true, cancellationToken), input,
                    StringComparison.OrdinalIgnoreCase)) mismatches.Add("communications input");
        }
        if (volume is not null || muted is not null)
        {
            var actual = await audioManager.GetMasterVolumeAsync(volumeDeviceId, cancellationToken);
            if (volume is { } expected && Math.Abs(actual.Volume - expected) > 0.011f) mismatches.Add("volume");
            if (muted is { } expectedMute && actual.Muted != expectedMute) mismatches.Add("mute");
        }
        return mismatches;
    }
}
