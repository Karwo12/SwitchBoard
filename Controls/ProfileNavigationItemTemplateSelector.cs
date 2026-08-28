using System.Windows;
using System.Windows.Controls;
using SwitchBoard.ViewModels;

namespace SwitchBoard.Controls;

public sealed class ProfileNavigationItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? CategoryTemplate { get; set; }

    public DataTemplate? ProfileTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item switch
    {
        CategoryItemViewModel => CategoryTemplate,
        ProfileItemViewModel => ProfileTemplate,
        _ => base.SelectTemplate(item, container)
    };
}
