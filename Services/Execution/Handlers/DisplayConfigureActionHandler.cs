using System.Text.Json.Nodes;
using SwitchBoard.Models.Actions;
using SwitchBoard.Services.Windows;

namespace SwitchBoard.Services.Execution.Handlers;

public sealed class DisplayConfigureActionHandler(
    IDisplayManager displayManager,
    IDisplayConfirmationService confirmationService) : IReversibleActionHandler
{
    private static readonly TimeSpan ConfirmationTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan GuardTimeout = TimeSpan.FromSeconds(22);

    public string ActionType => ActionTypeIds.DisplayConfigure;

    public async Task<JsonObject?> CaptureStateAsync(
        ActionDefinition action,
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var deviceName = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.DisplayDeviceName).Trim();
        var deviceId = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.DisplayDeviceId).Trim();
        if (string.IsNullOrWhiteSpace(deviceName)) throw new InvalidOperationException("A monitor must be selected.");
        return ToJson(await displayManager.GetCurrentStateAsync(deviceId, deviceName, cancellationToken));
    }

    public async Task<ActionExecutionResult> ExecuteAsync(
        ActionDefinition action,
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var deviceName = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.DisplayDeviceName).Trim();
        var deviceId = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.DisplayDeviceId).Trim();
        var width = ActionParameterReader.ReadInt32(action.Parameters, ActionParameterNames.DisplayWidth, 0);
        var height = ActionParameterReader.ReadInt32(action.Parameters, ActionParameterNames.DisplayHeight, 0);
        var refreshRate = ActionParameterReader.ReadInt32(action.Parameters, ActionParameterNames.DisplayRefreshRate, 0);
        var skipConfirmation = ActionParameterReader.ReadBoolean(
            action.Parameters, ActionParameterNames.SkipDisplayConfirmation, false);
        if (string.IsNullOrWhiteSpace(deviceName) || width <= 0 || height <= 0 || refreshRate <= 0)
        {
            return ActionExecutionResult.Failure("A monitor, resolution, and refresh rate must be selected.");
        }

        DisplayModeState? previous = null;
        DisplayRollbackGuardSession? guard = null;
        var temporaryModeApplied = false;
        var persistenceAttempted = false;
        try
        {
            previous = await displayManager.GetCurrentStateAsync(deviceId, deviceName, cancellationToken);
            if (previous.Width == width && previous.Height == height && previous.RefreshRate == refreshRate)
            {
                return ActionExecutionResult.Skipped("The selected display settings are already active.");
            }

            var target = previous with { Width = width, Height = height, RefreshRate = refreshRate };
            if (!skipConfirmation) guard = DisplayRollbackGuard.Start(previous, GuardTimeout);
            await displayManager.ApplyTemporaryAsync(target, cancellationToken);
            temporaryModeApplied = true;
            var applied = await displayManager.GetCurrentStateAsync(deviceId, deviceName, cancellationToken);
            if (!Matches(applied, target))
            {
                await displayManager.RestoreAsync(previous, CancellationToken.None);
                 guard?.Complete();
                return ActionExecutionResult.Failure("Windows did not apply the selected display settings.");
            }

            var keep = skipConfirmation || await confirmationService.ConfirmAsync(ConfirmationTimeout, cancellationToken);
             if (keep && guard?.ProtectionExpired == true)
            {
                keep = false;
            }
            if (!keep)
            {
                await displayManager.RestoreAsync(previous, CancellationToken.None);
                 guard?.Complete();
                cancellationToken.ThrowIfCancellationRequested();
                return ActionExecutionResult.Failure("The display settings were reverted.");
            }

            persistenceAttempted = true;
            await displayManager.PersistAsync(target, cancellationToken);
            var persisted = await displayManager.GetCurrentStateAsync(deviceId, deviceName, cancellationToken);
            if (!Matches(persisted, target))
            {
                await displayManager.PersistAsync(previous, CancellationToken.None);
                 guard?.Complete();
                return ActionExecutionResult.Failure("Windows did not keep the selected display settings.");
            }

             guard?.Complete();
            return ActionExecutionResult.Success(
                $"Verified: {width}×{height} at {refreshRate} Hz is active on {previous.DisplayName}.",
                ToJson(previous));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (temporaryModeApplied && previous is not null)
            {
                await TryRestoreAsync(previous, persistenceAttempted);
            }

            guard?.Complete();
            throw;
        }
        catch (Exception exception)
        {
            string? restoreError = null;
            if (temporaryModeApplied && previous is not null)
            {
                try
                {
                    if (persistenceAttempted) await displayManager.PersistAsync(previous, CancellationToken.None);
                    else await displayManager.RestoreAsync(previous, CancellationToken.None);
                }
                catch (Exception restoreException) { restoreError = restoreException.Message; }
            }

            guard?.Complete();
            return ActionExecutionResult.Failure(restoreError is null
                ? $"Could not change display settings: {exception.Message}"
                : $"Could not change display settings: {exception.Message} Rollback also failed: {restoreError}");
        }
        finally
        {
            guard?.Dispose();
        }
    }

    public async Task<ActionExecutionResult> RestoreAsync(
        ActionDefinition action,
        JsonObject restoreState,
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var state = FromJson(restoreState);
        if (state is null) return ActionExecutionResult.Failure("The saved display state is invalid.", false);
        var current = await displayManager.GetCurrentStateAsync(state.DeviceId, state.DeviceName, cancellationToken);
        if (Matches(current, state)) return ActionExecutionResult.Success("The saved display state was already active.");

        var skipConfirmation = ActionParameterReader.ReadBoolean(
            action.Parameters, ActionParameterNames.SkipDisplayConfirmation, false);
        using var guard = skipConfirmation ? null : DisplayRollbackGuard.Start(current, GuardTimeout);
        var persistenceAttempted = false;
        try
        {
            await displayManager.ApplyTemporaryAsync(state, cancellationToken);
            var applied = await displayManager.GetCurrentStateAsync(state.DeviceId, state.DeviceName, cancellationToken);
            if (!Matches(applied, state))
                throw new InvalidOperationException("Windows did not apply the saved display settings.");
            var keep = skipConfirmation || await confirmationService.ConfirmAsync(ConfirmationTimeout, cancellationToken);
             if (!keep || guard?.ProtectionExpired == true)
            {
                await displayManager.RestoreAsync(current, CancellationToken.None);
                 guard?.Complete();
                throw new InvalidOperationException("The restored display settings were reverted.");
            }
            persistenceAttempted = true;
            await displayManager.PersistAsync(state, cancellationToken);
            var verified = await displayManager.GetCurrentStateAsync(state.DeviceId, state.DeviceName, cancellationToken);
            if (!Matches(verified, state)) throw new InvalidOperationException("Windows did not keep the restored display settings.");
             guard?.Complete();
            return ActionExecutionResult.Success(
                $"Verified: {state.Width}×{state.Height} at {state.RefreshRate} Hz is active on {state.DisplayName}.");
        }
        catch
        {
            try
            {
                if (persistenceAttempted) await displayManager.PersistAsync(current, CancellationToken.None);
                else await displayManager.RestoreAsync(current, CancellationToken.None);
            }
            catch { }
             guard?.Complete();
            throw;
        }
    }

    private async Task TryRestoreAsync(DisplayModeState state, bool persist)
    {
        try
        {
            if (persist) await displayManager.PersistAsync(state, CancellationToken.None);
            else await displayManager.RestoreAsync(state, CancellationToken.None);
        }
        catch { }
    }

    private static bool Matches(DisplayModeState actual, DisplayModeState expected) =>
        actual.Width == expected.Width && actual.Height == expected.Height && actual.RefreshRate == expected.RefreshRate;

    private static JsonObject ToJson(DisplayModeState state) => new()
    {
        ["deviceName"] = state.DeviceName,
        ["deviceId"] = state.DeviceId,
        ["displayName"] = state.DisplayName,
        ["width"] = state.Width,
        ["height"] = state.Height,
        ["refreshRate"] = state.RefreshRate,
        ["bitsPerPixel"] = state.BitsPerPixel,
        ["positionX"] = state.PositionX,
        ["positionY"] = state.PositionY,
        ["orientation"] = state.Orientation,
        ["fixedOutput"] = state.FixedOutput
    };

    private static DisplayModeState? FromJson(JsonObject value)
    {
        try
        {
            return new DisplayModeState(
                value["deviceName"]?.GetValue<string>() ?? string.Empty,
                value["deviceId"]?.GetValue<string>() ?? string.Empty,
                value["displayName"]?.GetValue<string>() ?? string.Empty,
                value["width"]?.GetValue<int>() ?? 0,
                value["height"]?.GetValue<int>() ?? 0,
                value["refreshRate"]?.GetValue<int>() ?? 0,
                value["bitsPerPixel"]?.GetValue<int>() ?? 32,
                value["positionX"]?.GetValue<int>() ?? 0,
                value["positionY"]?.GetValue<int>() ?? 0,
                value["orientation"]?.GetValue<int>() ?? 0,
                value["fixedOutput"]?.GetValue<int>() ?? 0);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
