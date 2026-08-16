using System.Windows;
using SwitchBoard.Data;
using SwitchBoard.Localization;
using SwitchBoard.Themes;
using SwitchBoard.Views;

namespace SwitchBoard.Services;

public sealed class WpfCustomThemeEditorService(AppDataPaths paths, ILocalizationService localization)
    : ICustomThemeEditorService
{
    public CustomThemeSettings? Edit(CustomThemeSettings current, Action<CustomThemeSettings> livePreview)
    {
        var window = new CustomThemeWindow(current, paths, localization, livePreview)
        {
            Owner = Application.Current.MainWindow
        };
        return window.ShowDialog() == true ? window.Result : null;
    }
}
