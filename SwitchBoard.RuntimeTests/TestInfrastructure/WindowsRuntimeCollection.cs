namespace SwitchBoard.RuntimeTests.TestInfrastructure;

[CollectionDefinition("Windows runtime", DisableParallelization = true)]
public sealed class WindowsRuntimeCollection : ICollectionFixture<WindowsRuntimeFixture>
{
}


public sealed class WindowsRuntimeFixture
{
    public bool IsAdministrator { get; } =
        OperatingSystem.IsWindows() &&
        new System.Security.Principal.WindowsPrincipal(
            System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
}
