using SwitchBoard.Themes;

namespace SwitchBoard.Services;

public interface ICustomThemeEditorService
{
    CustomThemeSettings? Edit(CustomThemeSettings current, Action<CustomThemeSettings> livePreview);
}
