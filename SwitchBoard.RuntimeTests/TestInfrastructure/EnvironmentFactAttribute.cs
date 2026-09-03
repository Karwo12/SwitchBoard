using Xunit.Abstractions;
using Xunit.Sdk;

namespace SwitchBoard.RuntimeTests.TestInfrastructure;

[XunitTestCaseDiscoverer(
    "SwitchBoard.RuntimeTests.TestInfrastructure.EnvironmentFactDiscoverer",
    "SwitchBoard.RuntimeTests")]
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class EnvironmentFactAttribute(string requirement) : FactAttribute
{
    public string Requirement { get; } = requirement;
}

public sealed class EnvironmentFactDiscoverer(IMessageSink diagnosticMessageSink) : IXunitTestCaseDiscoverer
{
    public IEnumerable<IXunitTestCase> Discover(ITestFrameworkDiscoveryOptions discoveryOptions,
        ITestMethod testMethod, IAttributeInfo factAttribute)
    {
        var requirement = factAttribute.GetConstructorArguments().Single()?.ToString()
            ?? throw new InvalidOperationException("EnvironmentFact requires a requirement name.");
        yield return new EnvironmentTestCase(diagnosticMessageSink, testMethod, requirement);
    }
}

public sealed class EnvironmentTestCase : XunitTestCase
{
#pragma warning disable CS0618 // xUnit v2 requires the parameterless constructor for deserialization.
    public EnvironmentTestCase()
    {
    }

    public EnvironmentTestCase(IMessageSink diagnosticMessageSink, ITestMethod testMethod, string requirement)
        : base(diagnosticMessageSink, TestMethodDisplay.ClassAndMethod, TestMethodDisplayOptions.None, testMethod, [])
    {
        Requirement = requirement;
    }
#pragma warning restore CS0618

    public string Requirement { get; private set; } = string.Empty;

    public override Task<RunSummary> RunAsync(IMessageSink diagnosticMessageSink, IMessageBus messageBus,
        object[] constructorArguments, ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource) =>
        new EnvironmentTestCaseRunner(this, DisplayName, SkipReason, constructorArguments, TestMethodArguments,
            messageBus, aggregator, cancellationTokenSource).RunAsync();

    public override void Serialize(IXunitSerializationInfo data)
    {
        base.Serialize(data);
        data.AddValue(nameof(Requirement), Requirement);
    }

    public override void Deserialize(IXunitSerializationInfo data)
    {
        base.Deserialize(data);
        Requirement = data.GetValue<string>(nameof(Requirement));
    }
}

internal sealed class EnvironmentTestCaseRunner(
    EnvironmentTestCase testCase,
    string displayName,
    string? skipReason,
    object[] constructorArguments,
    object[] testMethodArguments,
    IMessageBus messageBus,
    ExceptionAggregator aggregator,
    CancellationTokenSource cancellationTokenSource)
    : XunitTestCaseRunner(testCase, displayName, skipReason, constructorArguments, testMethodArguments,
        messageBus, aggregator, cancellationTokenSource)
{
    protected override Task<RunSummary> RunTestAsync()
    {
        var reason = EnvironmentRequirements.GetSkipReason(testCase.Requirement);
        if (reason is null)
            return base.RunTestAsync();

        var test = CreateTest(testCase, DisplayName);
        MessageBus.QueueMessage(new TestSkipped(test, reason));
        return Task.FromResult(new RunSummary { Total = 1, Skipped = 1 });
    }
}

internal static class EnvironmentRequirements
{
    public static string? GetSkipReason(string requirement)
    {
        if (!OperatingSystem.IsWindows()) return "The integration test requires Windows.";
        return requirement switch
        {
            "Administrator" when !IsAdministrator() => "The integration test requires Administrator privileges.",
            "AudioEndpoint" when !HasAudioEndpoint() => "The test environment has no compatible Core Audio endpoint.",
            "Display" when !HasDisplay() => "The test environment exposes no display.",
            "AlternateDisplayMode" when !HasAlternateDisplayMode() => "The test environment exposes no alternate display mode.",
            "DisplayApply" when !AllowsRealSystemIntegration() || !HasAlternateDisplayMode() =>
                "The integration test needs an opt-in environment capable of applying a display mode.",
            "Notepad" when !HasNotepad() => "Notepad is not available in the current test environment.",
            "Edge" when !HasEdge() => "Microsoft Edge is not available in the current test environment.",
            "EdgeProcessTree" when !AllowsRealSystemIntegration() || !HasEdge() =>
                "The Edge process-tree test requires SWITCHBOARD_RUN_REAL_SYSTEM_TESTS=1 on a dedicated machine.",
            "PowerShellExecution" when !AllowsRealSystemIntegration() =>
                "The PowerShell process test requires SWITCHBOARD_RUN_REAL_SYSTEM_TESTS=1 on a dedicated machine.",
            "PowerQoS" when !HasPowerQoS() => "The Power QoS API is unavailable in this Windows environment.",
            "RunningService" when !HasRunningService() => "The test environment exposes no running Windows service.",
            "CurrentCatalogIconSmoke" when !AllowsCurrentCatalogIconSmoke() =>
                "The current-catalog icon smoke test requires SWITCHBOARD_RUN_CURRENT_CATALOG_ICON_SMOKE=1.",
            _ => null
        };
    }

    private static bool IsAdministrator() => new System.Security.Principal.WindowsPrincipal(
        System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(
            System.Security.Principal.WindowsBuiltInRole.Administrator);

    private static bool AllowsRealSystemIntegration() => string.Equals(
        Environment.GetEnvironmentVariable("SWITCHBOARD_RUN_REAL_SYSTEM_TESTS"), "1", StringComparison.Ordinal);

    private static bool AllowsCurrentCatalogIconSmoke() => string.Equals(
        Environment.GetEnvironmentVariable("SWITCHBOARD_RUN_CURRENT_CATALOG_ICON_SMOKE"), "1", StringComparison.Ordinal);

    private static bool HasAudioEndpoint()
    {
        try { return new WindowsAudioManager().GetDevicesAsync().GetAwaiter().GetResult().Count > 0; }
        catch (Exception exception) when (exception is PlatformNotSupportedException or Win32Exception or
                                          System.Runtime.InteropServices.COMException) { return false; }
    }

    private static bool HasDisplay()
    {
        try { return new WindowsDisplayManager().GetDisplaysAsync().GetAwaiter().GetResult().Count > 0; }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException) { return false; }
    }

    private static bool HasAlternateDisplayMode()
    {
        try
        {
            return new WindowsDisplayManager().GetDisplaysAsync().GetAwaiter().GetResult().Any(display =>
                display.Modes.Any(mode => mode.Width != display.CurrentWidth ||
                                         mode.Height != display.CurrentHeight ||
                                         mode.RefreshRate != display.CurrentRefreshRate));
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException) { return false; }
    }

    private static bool HasNotepad()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return File.Exists(Path.Combine(windows, "notepad.exe"));
    }

    private static bool HasEdge()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };
        return roots.Where(root => !string.IsNullOrWhiteSpace(root)).Select(root =>
            Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe"))
            .Any(File.Exists);
    }

    private static bool HasPowerQoS()
    {
        try
        {
            _ = new ProcessSettingsService().Capture(Process.GetCurrentProcess(), new JsonObject
            { [ActionParameterNames.ProcessPerformanceMode] = ProcessPerformanceModeIds.HighPerformance });
            return true;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or
                                          NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HasRunningService()
    {
        try
        {
            return new WindowsServiceManager().GetServicesAsync().GetAwaiter().GetResult()
                .Any(service => string.Equals(service.Status, "Running", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or
                                          UnauthorizedAccessException) { return false; }
    }
}
