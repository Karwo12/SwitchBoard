namespace SwitchBoard.Models.Actions;

public static class ActionTypeIds
{
    public const string ProcessSetState = "process.setState";
    public const string ProgramRun = "program.run";
    public const string ServiceSetState = "service.setState";
    public const string DisplayConfigure = "display.configure";
    public const string PowerSetPlan = "power.setPlan";
    public const string ScriptRun = "script.run";
    public const string Delay = "delay";
    public const string ProcessConfigure = "process.configure";
    public const string WaitProcessStart = "wait.processStart";
    public const string WaitProcessExit = "wait.processExit";
    public const string WaitWindow = "wait.window";
    public const string AudioConfigure = "audio.configure";
    public const string DeviceSetState = "device.setState";
    public const string ProfileRun = "profile.run";
    public const string ConditionIf = "condition.if";
    public const string NotificationShow = "notification.show";

    public static IReadOnlyList<string> All { get; } =
    [
        ProcessSetState,
        ProgramRun,
        ServiceSetState,
        DisplayConfigure,
        PowerSetPlan,
        ScriptRun,
        Delay,
        ProcessConfigure,
        WaitProcessStart,
        WaitProcessExit,
        WaitWindow,
        AudioConfigure,
        DeviceSetState,
        ProfileRun,
        ConditionIf,
        NotificationShow
    ];
}
