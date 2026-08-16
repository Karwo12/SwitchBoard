using System.Windows;
using SwitchBoard.Data;
using SwitchBoard.Localization;
using SwitchBoard.Themes;
using SwitchBoard.Views;

namespace SwitchBoard.Services;

public sealed class WpfCustomThemeEditorService(AppDataPaths paths, ILocalizationService localization)
    : ICustomThemeEditorService
{
    public CustomThemeEditResult? Edit(CustomThemeEditRequest request)
    {
        var window = new CustomThemeWindow(request, paths, localization)
        {
            Owner = Application.Current.MainWindow
        };
        return window.ShowDialog() == true ? window.Result : null;
    }

    public string? Rename(string currentName, IReadOnlyCollection<string> unavailableNames)
    {
        var window = new ThemeNameWindow(currentName, unavailableNames, localization)
        {
            Owner = Application.Current.MainWindow
        };
        return window.ShowDialog() == true ? window.Result : null;
    }
}
