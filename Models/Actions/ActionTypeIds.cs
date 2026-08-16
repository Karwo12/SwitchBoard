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

    public static IReadOnlyList<string> All { get; } =
    [
        ProcessSetState,
        ProgramRun,
        ServiceSetState,
        DisplayConfigure,
        PowerSetPlan,
        ScriptRun,
        Delay
    ];
}
