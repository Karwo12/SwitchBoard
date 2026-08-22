namespace SwitchBoard.RuntimeTests.TestInfrastructure;

public sealed class RuntimeTestContext : IDisposable
{
    public RuntimeTestContext()
    {
        Root = Path.Combine(Path.GetTempPath(), $"SwitchBoard-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
        AppDataRoot = Path.Combine(Root, "appdata");
        SessionRepository = new JsonExecutionSessionRepository(new AppDataPaths(AppDataRoot));
        ServiceManager = new WindowsServiceManager();
        PowerManager = new WindowsPowerPlanManager();
        DisplayManager = new WindowsDisplayManager();
        RestoreOrder = [];
        ReversibleHandler = new TestReversibleHandler(RestoreOrder, SessionRepository);
        Registry = new ActionRegistry(
        [
            new ProgramRunActionHandler(),
            new ProcessSetStateActionHandler(),
            new ServiceSetStateActionHandler(ServiceManager),
            new PowerSetPlanActionHandler(PowerManager),
            new ScriptRunActionHandler(),
            new DelayActionHandler(),
            ReversibleHandler
        ]);
        Runner = new ProfileRunner(Registry, SessionRepository);
    }

    public string Root { get; }
    public string AppDataRoot { get; }
    public JsonExecutionSessionRepository SessionRepository { get; }
    public WindowsServiceManager ServiceManager { get; }
    public WindowsPowerPlanManager PowerManager { get; }
    public WindowsDisplayManager DisplayManager { get; }
    public List<string> RestoreOrder { get; }
    public TestReversibleHandler ReversibleHandler { get; }
    public ActionRegistry Registry { get; }
    public ProfileRunner Runner { get; }

    public static ActionDefinition Action(string type, JsonObject parameters,
        ActionFailurePolicy failurePolicy = ActionFailurePolicy.Continue) =>
        new() { Type = type, Parameters = parameters, FailurePolicy = failurePolicy };

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SB_TEST_OUTPUT", null);
        Environment.SetEnvironmentVariable("SB_TEST_BATCH_OUTPUT", null);
        SessionRepository.Dispose();
        try { Directory.Delete(Root, true); } catch { }
    }
}

public abstract class RuntimeTestBase
{
    protected static ActionDefinition Action(string type, JsonObject parameters,
        ActionFailurePolicy failurePolicy = ActionFailurePolicy.Continue) =>
        RuntimeTestContext.Action(type, parameters, failurePolicy);
}
