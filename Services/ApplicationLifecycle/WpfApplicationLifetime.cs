using System.Windows;

namespace SwitchBoard.Services.ApplicationLifecycle;

public sealed class WpfApplicationLifetime : IApplicationLifetime
{
    public void Shutdown() => Application.Current.Shutdown();
}
