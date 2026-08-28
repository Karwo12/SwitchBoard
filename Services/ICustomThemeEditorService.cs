using SwitchBoard.Themes;

namespace SwitchBoard.Services;

public interface ICustomThemeEditorService
{
    Task<CustomThemeEditResult?> EditAsync(CustomThemeEditRequest request);

    string? Rename(string currentName, IReadOnlyCollection<string> unavailableNames);
}

public enum CustomThemeEditMode { Add, EditCustom, CopyBuiltIn, DuplicateCustom }

public sealed record CustomThemeEditRequest(
    CustomThemeEditMode Mode,
    string Name,
    CustomThemeSettings Colors,
    IReadOnlyCollection<string> UnavailableNames,
    string? ThemeId = null,
    Action<CustomThemeSettings>? ApplyTemporary = null);

public sealed record CustomThemeEditResult(string Name, CustomThemeSettings Colors);
