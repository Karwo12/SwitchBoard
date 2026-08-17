using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using SwitchBoard.Models.Actions;

namespace SwitchBoard.Services.Execution.Handlers;

public sealed class ProcessConfigureActionHandler : IReversibleActionHandler
{
    public string ActionType => ActionTypeIds.ProcessConfigure;

    public Task<JsonObject?> CaptureStateAsync(ActionDefinition action, ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = FindSingle(action);
        return Task.FromResult<JsonObject?>(new JsonObject
        {
            ["processId"] = process.Id,
            ["startedAtUtcTicks"] = process.StartTime.ToUniversalTime().Ticks,
            ["affinityMask"] = process.ProcessorAffinity.ToInt64(),
            ["priority"] = process.PriorityClass.ToString()
        });
    }

    public Task<ActionExecutionResult> ExecuteAsync(ActionDefinition action, ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var process = FindSingle(action);
            var changeAffinity = ActionParameterReader.ReadBoolean(action.Parameters,
                ActionParameterNames.ChangeAffinity, false);
            var changePriority = ActionParameterReader.ReadBoolean(action.Parameters,
                ActionParameterNames.ChangePriority, false);
            if (!changeAffinity && !changePriority)
                return Task.FromResult(ActionExecutionResult.Failure("No process setting was selected."));

            if (changeAffinity)
            {
                var mask = ReadAffinityMask(action.Parameters[ActionParameterNames.CpuIndices] as JsonArray);
                if (mask == 0) return Task.FromResult(ActionExecutionResult.Failure("CPU affinity cannot disable every logical processor."));
                process.ProcessorAffinity = new IntPtr(unchecked((long)mask));
                process.Refresh();
                if (unchecked((ulong)process.ProcessorAffinity.ToInt64()) != mask)
                    return Task.FromResult(ActionExecutionResult.Failure(
                        $"Windows did not apply the requested CPU affinity. Current mask: 0x{process.ProcessorAffinity.ToInt64():X}."));
            }
            if (changePriority)
            {
                var expectedPriority = ParsePriority(ActionParameterReader.ReadString(action.Parameters,
                    ActionParameterNames.ProcessPriority));
                process.PriorityClass = expectedPriority;
                process.Refresh();
                if (process.PriorityClass != expectedPriority)
                    return Task.FromResult(ActionExecutionResult.Failure(
                        $"Windows did not apply priority {expectedPriority}. Current priority: {process.PriorityClass}."));
            }

            return Task.FromResult(ActionExecutionResult.Success("Verified: the requested process settings are active."));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or
                                           Win32Exception or NotSupportedException)
        {
            return Task.FromResult(ActionExecutionResult.Failure($"Could not change process settings: {exception.Message}"));
        }
    }

    public Task<ActionExecutionResult> RestoreAsync(ActionDefinition action, JsonObject restoreState, ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var processId = restoreState["processId"]?.GetValue<int>() ?? 0;
        var startedAt = restoreState["startedAtUtcTicks"]?.GetValue<long>() ?? 0;
        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != startedAt)
                throw new InvalidOperationException("The original process no longer exists; its settings cannot be restored.");
            if (restoreState["affinityMask"]?.GetValue<long>() is { } mask)
            {
                process.ProcessorAffinity = new IntPtr(mask);
                process.Refresh();
                if (process.ProcessorAffinity.ToInt64() != mask)
                    return Task.FromResult(ActionExecutionResult.Failure("Windows did not restore the previous CPU affinity."));
            }
            if (restoreState["priority"]?.GetValue<string>() is { } priority &&
                Enum.TryParse<ProcessPriorityClass>(priority, out var parsed))
            {
                process.PriorityClass = parsed;
                process.Refresh();
                if (process.PriorityClass != parsed)
                    return Task.FromResult(ActionExecutionResult.Failure("Windows did not restore the previous process priority."));
            }
            return Task.FromResult(ActionExecutionResult.Success("Verified: the previous process settings are active."));
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException("The original process no longer exists; its settings cannot be restored.");
        }
        finally { process?.Dispose(); }
    }

    public static ulong ReadAffinityMask(JsonArray? values)
    {
        if (values is null) return 0;
        ulong mask = 0;
        var supported = Math.Min(Environment.ProcessorCount, IntPtr.Size * 8);
        foreach (var node in values)
        {
            if (node is null) continue;
            int cpu;
            try { cpu = node.GetValue<int>(); }
            catch (InvalidOperationException) { continue; }
            if (cpu >= 0 && cpu < supported) mask |= 1UL << cpu;
        }
        return mask;
    }

    private static Process FindSingle(ActionDefinition action)
    {
        var name = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.ProcessName);
        var path = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.ExecutablePath);
        var matches = ProcessTargetResolver.Find(name, path, action.RuntimeProcessIdHint);
        if (matches.Count == 0) throw new InvalidOperationException("The selected process is not running.");
        var selected = matches[0];
        foreach (var extra in matches.Skip(1)) extra.Dispose();
        return selected;
    }

    private static ProcessPriorityClass ParsePriority(string value) => value switch
    {
        ProcessPriorityIds.Idle => ProcessPriorityClass.Idle,
        ProcessPriorityIds.BelowNormal => ProcessPriorityClass.BelowNormal,
        ProcessPriorityIds.AboveNormal => ProcessPriorityClass.AboveNormal,
        ProcessPriorityIds.High => ProcessPriorityClass.High,
        _ => ProcessPriorityClass.Normal
    };
}
