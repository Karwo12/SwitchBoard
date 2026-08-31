using System.Windows.Controls;

namespace SwitchBoard.Views.Panels;

/// <summary>
/// The process template is entirely declarative. This class intentionally owns
/// no item-container or visual-tree lifecycle logic.
/// </summary>
public partial class PerformancePanel : UserControl
{
    public PerformancePanel() => InitializeComponent();

    public void ScrollToTop()
    {
        if (ProcessItems.Items.Count > 0)
            ProcessItems.ScrollIntoView(ProcessItems.Items[0]);
    }
}
