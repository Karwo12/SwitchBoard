namespace SwitchBoard.Models.Actions;

public static class ProcessPriorityIds
{
    public const string NoChange = "noChange";
    public const string Idle = "idle";
    public const string BelowNormal = "belowNormal";
    public const string Normal = "normal";
    public const string AboveNormal = "aboveNormal";
    public const string High = "high";
}

public static class ProcessMemoryPriorityIds
{
    public const string NoChange = "noChange";
    public const string VeryLow = "veryLow";
    public const string Low = "low";
    public const string Medium = "medium";
    public const string BelowNormal = "belowNormal";
    public const string Normal = "normal";
}

public static class ProcessPerformanceModeIds
{
    public const string NoChange = "noChange";
    public const string WindowsDefault = "windowsDefault";
    public const string HighPerformance = "highPerformance";
    public const string Efficiency = "efficiency";
}

public static class WindowMatchModeIds
{
    public const string Any = "any";
    public const string Contains = "contains";
    public const string Exact = "exact";
}

public static class WindowBehaviorIds
{
    public const string None = "none";
    public const string Minimize = "minimize";
    public const string Maximize = "maximize";
    public const string Restore = "restore";
    public const string Hide = "hide";
}

public static class InstanceBehaviorIds
{
    public const string DoNotStartAgain = "doNotStartAgain";
    public const string StartAnother = "startAnother";
    public const string RestartExisting = "restartExisting";
}

public static class DeviceStateIds
{
    public const string Enabled = "enabled";
    public const string Disabled = "disabled";
    public const string Unchanged = "unchanged";
}

public static class ConditionTypeIds
{
    public const string ProcessRunning = "processRunning";
    public const string ProcessNotRunning = "processNotRunning";
    public const string ServiceRunning = "serviceRunning";
    public const string ServiceStopped = "serviceStopped";
    public const string FileExists = "fileExists";
    public const string FileNotExists = "fileNotExists";
}

public static class NotificationLevelIds
{
    public const string Info = "info";
    public const string Success = "success";
    public const string Warning = "warning";
    public const string Error = "error";
}
