using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SwitchBoard.ViewModels;

namespace SwitchBoard.Views.Panels;

public partial class ActivityPanel : UserControl
{
    public ActivityPanel() => InitializeComponent();

    public event EventHandler<ActivityNavigationRequestedEventArgs>? NavigationRequested;

    private void SystemChangeEntry_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not FrameworkElement
            { DataContext: SystemChangeItemViewModel entry }) return;
        NavigationRequested?.Invoke(this, new ActivityNavigationRequestedEventArgs(entry.ProfileId, entry.ActionId));
    }

    private void SystemChangeTarget_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SystemChangeItemViewModel entry } || !entry.IsNavigable)
            return;
        NavigationRequested?.Invoke(this, new ActivityNavigationRequestedEventArgs(entry.ProfileId, entry.ActionId));
        e.Handled = true;
    }

    private void HistoryAction_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ProfileExecutionActionViewModel entry }) return;
        NavigationRequested?.Invoke(this, new ActivityNavigationRequestedEventArgs(entry.ProfileId, entry.ActionId));
        e.Handled = true;
    }
}

public sealed class ActivityNavigationRequestedEventArgs(Guid? profileId, Guid actionId) : EventArgs
{
    public Guid? ProfileId { get; } = profileId;
    public Guid ActionId { get; } = actionId;
}
