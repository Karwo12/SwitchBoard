using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using SwitchBoard.Models.Actions;

namespace SwitchBoard.Services.Execution.Handlers;

/// <summary>
/// The shared implementation for process priority, memory priority and CPU affinity.
/// A property is read, changed and restored only when the action explicitly selected it.
/// </summary>
public sealed class ProcessSettingsService
{
    public static ProcessSettingsService Shared { get; } = new();

    private const int ProcessMemoryPriorityInformation = 0;
    private const uint ProcessMemoryPriorityInformationSize = sizeof(uint);
    private const int ProcessPowerThrottlingInformation = 4;
    // Some Windows builds return ERROR_INVALID_PARAMETER for the documented
    // GetProcessInformation(ProcessPowerThrottling) query even though the
    // corresponding SetProcessInformation call is supported.
    private const int ProcessPowerThrottlingStateInformation = 77;
    private const int ErrorInvalidParameter = 87;
    private const uint ProcessPowerThrottlingInformationSize = sizeof(uint) * 3;
    private const uint ProcessPowerThrottlingCurrentVersion = 1;
    private const uint ProcessPowerThrottlingExecutionSpeed = 0x1;

    public void Apply(Process process, JsonObject parameters)
    {
        var changeAffinity = ActionParameterReader.ReadBoolean(parameters,
            ActionParameterNames.ChangeAffinity, false);
        var changePriority = ShouldChangeProcessPriority(parameters);
        var memoryPriority = ActionParameterReader.ReadString(parameters,
            ActionParameterNames.ProcessMemoryPriority);
        var changeMemoryPriority = IsConcreteMemoryPriority(memoryPriority);
        var performanceMode = ActionParameterReader.ReadString(parameters,
            ActionParameterNames.ProcessPerformanceMode);
        var changePerformanceMode = IsConcretePerformanceMode(performanceMode);
        if (!changeAffinity && !changePriority && !changeMemoryPriority && !changePerformanceMode)
            throw new InvalidOperationException("No process setting was selected.");

        if (changeAffinity)
        {
            var mask = ReadAffinityMask(parameters[ActionParameterNames.CpuIndices] as JsonArray);
            if (mask == 0)
                throw new InvalidOperationException("CPU affinity cannot disable every logical processor.");
            process.ProcessorAffinity = new IntPtr(unchecked((long)mask));
            process.Refresh();
            if (unchecked((ulong)process.ProcessorAffinity.ToInt64()) != mask)
                throw new InvalidOperationException(
                    $"Windows did not apply the requested CPU affinity. Current mask: 0x{process.ProcessorAffinity.ToInt64():X}.");
        }

        if (changePriority)
        {
            var expectedPriority = ParsePriority(ActionParameterReader.ReadString(parameters,
                ActionParameterNames.ProcessPriority));
            process.PriorityClass = expectedPriority;
            process.Refresh();
            if (process.PriorityClass != expectedPriority)
                throw new InvalidOperationException(
                    $"Windows did not apply priority {expectedPriority}. Current priority: {process.PriorityClass}.");
        }

        if (changeMemoryPriority)
            SetMemoryPriority(process, ParseMemoryPriorityValue(memoryPriority));

        if (changePerformanceMode)
            SetPerformanceMode(process, performanceMode);
    }

    public JsonObject Capture(Process process, JsonObject parameters)
    {
        var state = new JsonObject
        {
            ["processId"] = process.Id,
            ["startedAtUtcTicks"] = process.StartTime.ToUniversalTime().Ticks
        };

        if (ActionParameterReader.ReadBoolean(parameters, ActionParameterNames.ChangeAffinity, false))
            state["affinityMask"] = process.ProcessorAffinity.ToInt64();

        if (ShouldChangeProcessPriority(parameters))
            state["priority"] = process.PriorityClass.ToString();

        if (IsConcreteMemoryPriority(ActionParameterReader.ReadString(parameters,
                ActionParameterNames.ProcessMemoryPriority)))
            state["memoryPriority"] = (int)GetMemoryPriority(process);

        if (IsConcretePerformanceMode(ActionParameterReader.ReadString(parameters,
                ActionParameterNames.ProcessPerformanceMode)))
        {
            var performanceState = GetPerformanceModeState(process);
            state["performanceControlMask"] = performanceState.ControlMask;
            state["performanceStateMask"] = performanceState.StateMask;
        }

        return state;
    }

    public void Restore(Process process, JsonObject state)
    {
        if (state["affinityMask"]?.GetValue<long>() is { } mask)
        {
            process.ProcessorAffinity = new IntPtr(mask);
            process.Refresh();
            if (process.ProcessorAffinity.ToInt64() != mask)
                throw new InvalidOperationException("Windows did not restore the previous CPU affinity.");
        }
        if (state["priority"]?.GetValue<string>() is { } priority &&
            Enum.TryParse<ProcessPriorityClass>(priority, out var parsed))
        {
            process.PriorityClass = parsed;
            process.Refresh();
            if (process.PriorityClass != parsed)
                throw new InvalidOperationException("Windows did not restore the previous process priority.");
        }
        if (state["memoryPriority"]?.GetValue<int>() is { } memoryPriority)
            SetMemoryPriority(process, (uint)memoryPriority);
        if (state["performanceControlMask"]?.GetValue<uint>() is { } controlMask &&
            state["performanceStateMask"]?.GetValue<uint>() is { } stateMask)
            SetPerformanceModeState(process, controlMask, stateMask);
    }

    public static bool ShouldChangeProcessPriority(JsonObject parameters) =>
        ActionParameterReader.ReadBoolean(parameters, ActionParameterNames.ChangePriority, false) &&
        !string.Equals(ActionParameterReader.ReadString(parameters, ActionParameterNames.ProcessPriority),
            ProcessPriorityIds.NoChange, StringComparison.OrdinalIgnoreCase);

    public static bool IsConcreteMemoryPriority(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !string.Equals(value, ProcessMemoryPriorityIds.NoChange, StringComparison.OrdinalIgnoreCase);

    public static bool IsConcretePerformanceMode(string value) =>
        value is ProcessPerformanceModeIds.WindowsDefault or ProcessPerformanceModeIds.HighPerformance or
            ProcessPerformanceModeIds.Efficiency;

    public static (uint ControlMask, uint StateMask) PerformanceMasksFor(string value) => value switch
    {
        ProcessPerformanceModeIds.WindowsDefault => (0, 0),
        ProcessPerformanceModeIds.HighPerformance =>
            (ProcessPowerThrottlingExecutionSpeed, 0),
        ProcessPerformanceModeIds.Efficiency =>
            (ProcessPowerThrottlingExecutionSpeed, ProcessPowerThrottlingExecutionSpeed),
        _ => throw new InvalidOperationException("An unsupported process performance mode was selected.")
    };

    public static ulong ReadAffinityMask(JsonArray? values)
    {
        if (values is null) return 0;
        ulong mask = 0;
        var supported = Math.Min(Environment.ProcessorCount, IntPtr.Size * 8);
        foreach (var node in values)
        {
            if (node is null) continue;
            try
            {
                var cpu = node.GetValue<int>();
                if (cpu >= 0 && cpu < supported) mask |= 1UL << cpu;
            }
            catch (InvalidOperationException) { }
        }
        return mask;
    }

    private static ProcessPriorityClass ParsePriority(string value) => value switch
    {
        ProcessPriorityIds.Idle => ProcessPriorityClass.Idle,
        ProcessPriorityIds.BelowNormal => ProcessPriorityClass.BelowNormal,
        ProcessPriorityIds.AboveNormal => ProcessPriorityClass.AboveNormal,
        ProcessPriorityIds.High => ProcessPriorityClass.High,
        _ => ProcessPriorityClass.Normal
    };

    public static uint ParseMemoryPriorityValue(string value) => value switch
    {
        ProcessMemoryPriorityIds.VeryLow => 1,
        ProcessMemoryPriorityIds.Low => 2,
        ProcessMemoryPriorityIds.Medium => 3,
        ProcessMemoryPriorityIds.BelowNormal => 4,
        ProcessMemoryPriorityIds.Normal => 5,
        _ => throw new InvalidOperationException("An unsupported memory priority was selected.")
    };

    private static uint GetMemoryPriority(Process process)
    {
        if (!GetProcessInformation(process.Handle, ProcessMemoryPriorityInformation,
                out MemoryPriorityInformation information, ProcessMemoryPriorityInformationSize))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Windows could not read the process memory priority.");
        if (information.MemoryPriority is < 1 or > 5)
            throw new InvalidOperationException("Windows returned an unsupported process memory priority.");
        return information.MemoryPriority;
    }

    private static void SetMemoryPriority(Process process, uint priority)
    {
        if (priority is < 1 or > 5)
            throw new InvalidOperationException("An unsupported memory priority was selected.");
        var information = new MemoryPriorityInformation { MemoryPriority = priority };
        if (!SetProcessInformation(process.Handle, ProcessMemoryPriorityInformation,
                ref information, ProcessMemoryPriorityInformationSize))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Windows could not change the process memory priority.");
    }

    private static (uint ControlMask, uint StateMask) GetPerformanceModeState(Process process)
    {
        if (!GetProcessInformation(process.Handle, ProcessPowerThrottlingInformation,
                out ProcessPowerThrottlingState information, ProcessPowerThrottlingInformationSize))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorInvalidParameter || NtQueryInformationProcess(process.Handle,
                    ProcessPowerThrottlingStateInformation, out information,
                    ProcessPowerThrottlingInformationSize, out _) != 0)
                throw new Win32Exception(error, "Windows could not read the process performance mode.");
        }
        if (information.Version == 0)
            throw new InvalidOperationException("Windows returned an invalid process performance mode.");
        return (information.ControlMask, information.StateMask);
    }

    private static void SetPerformanceMode(Process process, string mode)
    {
        var (controlMask, stateMask) = PerformanceMasksFor(mode);
        SetPerformanceModeState(process, controlMask, stateMask);
    }

    private static void SetPerformanceModeState(Process process, uint controlMask, uint stateMask)
    {
        var information = new ProcessPowerThrottlingState
        {
            Version = ProcessPowerThrottlingCurrentVersion,
            ControlMask = controlMask,
            StateMask = stateMask
        };
        if (!SetProcessInformation(process.Handle, ProcessPowerThrottlingInformation,
                ref information, ProcessPowerThrottlingInformationSize))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Windows could not change the process performance mode.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryPriorityInformation
    {
        public uint MemoryPriority;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessInformation(
        IntPtr processHandle,
        int processInformationClass,
        out MemoryPriorityInformation processInformation,
        uint processInformationSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(
        IntPtr processHandle,
        int processInformationClass,
        ref MemoryPriorityInformation processInformation,
        uint processInformationSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessInformation(
        IntPtr processHandle,
        int processInformationClass,
        out ProcessPowerThrottlingState processInformation,
        uint processInformationSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(
        IntPtr processHandle,
        int processInformationClass,
        ref ProcessPowerThrottlingState processInformation,
        uint processInformationSize);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        out ProcessPowerThrottlingState processInformation,
        uint processInformationLength,
        out uint returnLength);
}
