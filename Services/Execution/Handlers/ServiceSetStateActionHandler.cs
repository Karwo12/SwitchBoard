using System.Text.Json.Nodes;
using SwitchBoard.Localization;
using SwitchBoard.Models.Actions;
using SwitchBoard.Services.Windows;

namespace SwitchBoard.Services.Execution.Handlers;

public sealed class ServiceSetStateActionHandler(IWindowsServiceManager serviceManager,
    ILocalizationService? localization = null) : IReversibleActionHandler
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public string ActionType => ActionTypeIds.ServiceSetState;

    public async Task<JsonObject?> CaptureStateAsync(ActionDefinition action, ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var serviceName = ReadServiceName(action);
        try
        {
            var snapshot = await serviceManager.GetSnapshotAsync(serviceName, cancellationToken);
            var capturedRuntime = RuntimeId(snapshot.RuntimeState);
            var capturedStartup = StartupId(snapshot.StartupType);
            if (capturedRuntime is not (ServiceDesiredStateIds.Running or ServiceDesiredStateIds.Stopped) ||
                capturedStartup is not (ServiceStartupTypeIds.Automatic or ServiceStartupTypeIds.AutomaticDelayed or
                    ServiceStartupTypeIds.Manual or ServiceStartupTypeIds.Disabled))
                throw new InvalidOperationException($"Service '{serviceName}' has a transitional or unsupported configuration.");
            return new JsonObject
            {
                ["serviceName"] = serviceName,
                ["serviceDisplayName"] = ActionParameterReader.ReadString(action.Parameters,
                    ActionParameterNames.ServiceDisplayName),
                ["previousState"] = capturedRuntime,
                ["previousStartupType"] = capturedStartup
            };
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException or
                                          System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException(Format("Result.ServiceReadFailed",
                "Could not read the state of service '{0}'.", Target(action, serviceName)), exception);
        }
    }

    public async Task<ActionExecutionResult> ExecuteAsync(ActionDefinition action, ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var serviceName = ReadServiceName(action);
        var desiredState = ReadRuntimeTarget(action);
        var desiredStartupType = ReadStartupTarget(action);
        if (desiredState == ServiceDesiredStateIds.Unchanged &&
            desiredStartupType == ServiceStartupTypeIds.Unchanged)
        {
            return ActionExecutionResult.Skipped(Format("Result.ServiceNothingRequested",
                "{0}: no service property was selected for change.", Target(action, serviceName)));
        }

        var result = await serviceManager.SetConfigurationAsync(serviceName, desiredState,
            desiredStartupType, Timeout(action), cancellationToken);
        return MapResult(result, Target(action, serviceName), restore: false);
    }

    public async Task<ActionExecutionResult> RestoreAsync(ActionDefinition action, JsonObject restoreState,
        ActionExecutionContext context, CancellationToken cancellationToken)
    {
        var serviceName = restoreState["serviceName"]?.GetValue<string>();
        var previousState = restoreState["previousState"]?.GetValue<string>();
        // Old sessions captured only runtime status and remain safely restorable.
        var previousStartupType = restoreState["previousStartupType"]?.GetValue<string>() ??
                                  ServiceStartupTypeIds.Unchanged;
        if (string.IsNullOrWhiteSpace(serviceName) || string.IsNullOrWhiteSpace(previousState))
            return ActionExecutionResult.Failure("The saved service state is incomplete.", false);

        var result = await serviceManager.SetConfigurationAsync(serviceName, previousState,
            previousStartupType, Timeout(action), cancellationToken);
        var displayName = restoreState["serviceDisplayName"]?.GetValue<string>();
        var target = string.IsNullOrWhiteSpace(displayName) ? serviceName : $"{displayName} ({serviceName})";
        return MapResult(result, target, restore: true);
    }

    private ActionExecutionResult MapResult(WindowsServiceConfigurationResult result, string target, bool restore)
    {
        var changed = HasObservedChange(result);
        var stateAfter = SnapshotJson(result.CurrentState);
        string message;
        if (!result.IsSuccessful && result.Win32Error == 5)
        {
            message = Format("Result.ServiceAdminRequiredDetailed",
                "Could not change service '{0}': administrator privileges are required. Expected: status {1}, startup {2}. Actual: status {3}, startup {4}.",
                target, DisplayRequestedRuntime(result), DisplayRequestedStartup(result),
                result.CurrentState?.RuntimeState ?? "unknown", result.CurrentState?.StartupType ?? "unknown");
        }
        else if (!result.IsSuccessful && result.WasRestartedByWindows)
        {
            message = Format("Result.ServiceRestarted",
                "Service '{0}' reached Stopped, but Windows started it again. Current state: {1}.",
                target, result.CurrentState?.RuntimeState ?? "unknown");
        }
        else if (!result.IsSuccessful)
        {
            message = Format("Result.ServiceConfigurationMismatch",
                "Windows did not apply the complete configuration of service '{0}'. Expected: status {1}, startup {2}. Actual: status {3}, startup {4}. {5}",
                target, DisplayRequestedRuntime(result), DisplayRequestedStartup(result),
                result.CurrentState?.RuntimeState ?? "unknown", result.CurrentState?.StartupType ?? "unknown",
                result.Message ?? string.Empty);
        }
        else if (result.IsSkipped)
        {
            message = Format("Result.ServiceConfigurationSkipped",
                "{0}: status {1}, startup {2} — already configured; skipped.", target,
                result.CurrentState?.RuntimeState ?? "unknown", result.CurrentState?.StartupType ?? "unknown");
        }
        else
        {
            message = Format(restore ? "Result.ServiceConfigurationRestored" : "Result.ServiceConfigurationVerified",
                restore
                    ? "{0} — restored. Status: {1} → {2}; startup: {3} → {4}."
                    : "{0} — verified. Status: {1} → {2}; startup: {3} → {4}.",
                target, result.StateBefore?.RuntimeState ?? "unknown", result.CurrentState?.RuntimeState ?? "unknown",
                result.StateBefore?.StartupType ?? "unknown", result.CurrentState?.StartupType ?? "unknown");
        }

        if (result.IsSkipped)
            return restore ? ActionExecutionResult.Success(message, stateAfter: stateAfter)
                : ActionExecutionResult.Skipped(message);
        if (result.IsSuccessful)
            return ActionExecutionResult.Success(message, restoreRequired: restore ? null : changed,
                stateAfter: stateAfter, technicalDetails: TechnicalDetails(target, result));
        return ActionExecutionResult.Failure(message, technicalDetails: TechnicalDetails(target, result),
            restoreRequired: restore ? null : changed, stateAfter: stateAfter);
    }

    private static bool HasObservedChange(WindowsServiceConfigurationResult result) =>
        result.StateBefore is not null && result.CurrentState is not null &&
        (!string.Equals(result.StateBefore.RuntimeState, result.CurrentState.RuntimeState,
             StringComparison.OrdinalIgnoreCase) ||
         !string.Equals(result.StateBefore.StartupType, result.CurrentState.StartupType,
             StringComparison.OrdinalIgnoreCase));

    private static JsonObject? SnapshotJson(WindowsServiceSnapshot? snapshot) => snapshot is null ? null : new JsonObject
    {
        ["runtimeState"] = RuntimeId(snapshot.RuntimeState),
        ["startupType"] = StartupId(snapshot.StartupType)
    };

    private static string RuntimeId(string value) => value switch
    {
        "Running" => ServiceDesiredStateIds.Running,
        "Stopped" => ServiceDesiredStateIds.Stopped,
        _ => value
    };

    private static string StartupId(string value) => value switch
    {
        "Automatic" => ServiceStartupTypeIds.Automatic,
        "Automatic (Delayed Start)" => ServiceStartupTypeIds.AutomaticDelayed,
        "Manual" => ServiceStartupTypeIds.Manual,
        "Disabled" => ServiceStartupTypeIds.Disabled,
        _ => value
    };

    private static string ReadServiceName(ActionDefinition action)
    {
        var value = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.ServiceName).Trim();
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException("A Windows service must be selected.")
            : value;
    }

    private static string ReadRuntimeTarget(ActionDefinition action)
    {
        var value = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.DesiredState);
        return string.IsNullOrWhiteSpace(value) ? ServiceDesiredStateIds.Unchanged : value;
    }

    private static string ReadStartupTarget(ActionDefinition action)
    {
        var value = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.ServiceStartupType);
        return string.IsNullOrWhiteSpace(value) ? ServiceStartupTypeIds.Unchanged : value;
    }

    private static TimeSpan Timeout(ActionDefinition action) =>
        action.Timeout is { } value && value > TimeSpan.Zero ? value : DefaultTimeout;

    private static string Target(ActionDefinition action, string serviceName)
    {
        var displayName = ActionParameterReader.ReadString(action.Parameters,
            ActionParameterNames.ServiceDisplayName).Trim();
        return string.IsNullOrWhiteSpace(displayName) ? serviceName : $"{displayName} ({serviceName})";
    }

    private static string DisplayRequestedRuntime(WindowsServiceConfigurationResult result) =>
        result.RequestedRuntimeState == ServiceDesiredStateIds.Unchanged
            ? result.StateBefore?.RuntimeState ?? "unchanged"
            : result.RequestedRuntimeState;

    private static string DisplayRequestedStartup(WindowsServiceConfigurationResult result) =>
        result.RequestedStartupType == ServiceStartupTypeIds.Unchanged
            ? result.StateBefore?.StartupType ?? "unchanged"
            : result.RequestedStartupType;

    private static string TechnicalDetails(string target, WindowsServiceConfigurationResult result) =>
        $"Target={target} BeforeStatus={result.StateBefore?.RuntimeState ?? "unknown"} " +
        $"BeforeStartup={result.StateBefore?.StartupType ?? "unknown"} " +
        $"RequestedStatus={result.RequestedRuntimeState} RequestedStartup={result.RequestedStartupType} " +
        $"ActualStatus={result.CurrentState?.RuntimeState ?? "unknown"} " +
        $"ActualStartup={result.CurrentState?.StartupType ?? "unknown"} " +
        $"Win32Error={result.Win32Error?.ToString() ?? "none"} RestartedByWindows={result.WasRestartedByWindows}";

    private string Format(string key, string fallback, params object?[] arguments) => localization is null
        ? string.Format(System.Globalization.CultureInfo.CurrentCulture, fallback, arguments)
        : localization.Format(key, arguments);
}
