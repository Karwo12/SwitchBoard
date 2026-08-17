using System.Text.Json.Nodes;
using SwitchBoard.Models.Actions;

namespace SwitchBoard.Services.Execution.Handlers;

public sealed class WaitWindowActionHandler : IActionHandler
{
    public string ActionType => ActionTypeIds.WaitWindow;

    public async Task<ActionExecutionResult> ExecuteAsync(ActionDefinition action, ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var process = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.ProcessName);
        var path = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.ExecutablePath);
        var mode = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.WindowMatchMode);
        var title = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.WindowTitle);
        if (string.IsNullOrWhiteSpace(process)) return ActionExecutionResult.Failure("Process name is required.");
        if (mode is WindowMatchModeIds.Contains or WindowMatchModeIds.Exact && string.IsNullOrWhiteSpace(title))
            return ActionExecutionResult.Failure("Window title is required for this match mode.");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var windows = WindowInterop.FindWindows(process, path, mode, title);
            if (windows.Count > 0) return ActionExecutionResult.Success("The requested window is ready.");
            await Task.Delay(200, cancellationToken);
        }
    }

    public Task<ActionExecutionResult> RestoreAsync(ActionDefinition action, JsonObject restoreState, ActionExecutionContext context,
        CancellationToken cancellationToken) => Task.FromResult(ActionExecutionResult.Skipped("Wait actions do not require restore."));
}
