using System.Windows.Controls;

namespace SwitchBoard.Views.Panels;

public partial class SystemPanel : UserControl
{
    public SystemPanel() => InitializeComponent();

    public void ScrollToTop() => ContentScrollViewer.ScrollToTop();
}
