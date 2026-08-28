namespace SwitchBoard.Localization;

public interface ILocalizationService
{
    IReadOnlyList<LanguageDefinition> AvailableLanguages { get; }

    string CurrentLanguageId { get; }

    string DetectSystemLanguage();

    string ApplyLanguage(string? languageId);

    string GetString(string resourceKey);

    string Format(string resourceKey, params object?[] arguments);
}
