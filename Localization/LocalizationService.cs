using System.Globalization;
using System.Windows;

namespace SwitchBoard.Localization;

public sealed class LocalizationService : ILocalizationService
{
    private readonly IReadOnlyList<LanguageDefinition> _availableLanguages =
    [
        Create(LanguageIds.English, "Language.English", "Strings.en.xaml"),
        Create(LanguageIds.Polish, "Language.Polish", "Strings.pl.xaml")
    ];

    public IReadOnlyList<LanguageDefinition> AvailableLanguages => _availableLanguages;

    public string CurrentLanguageId { get; private set; } = LanguageIds.English;

    public string DetectSystemLanguage() =>
        CultureInfo.CurrentUICulture.Name.StartsWith("pl", StringComparison.OrdinalIgnoreCase)
            ? LanguageIds.Polish
            : LanguageIds.English;

    public string ApplyLanguage(string? languageId)
    {
        var language = _availableLanguages.FirstOrDefault(candidate =>
                           string.Equals(candidate.Id, languageId, StringComparison.OrdinalIgnoreCase))
                       ?? _availableLanguages[0];

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var languageDictionaries = dictionaries
            .Where(dictionary => dictionary.Source is not null && IsLanguageResource(dictionary.Source))
            .ToList();

        if (languageDictionaries.Count != 1 ||
            !languageDictionaries[0].Source.OriginalString.EndsWith(
                GetResourceFileName(language.ResourceUri),
                StringComparison.OrdinalIgnoreCase))
        {
            foreach (var dictionary in languageDictionaries)
            {
                dictionaries.Remove(dictionary);
            }

            dictionaries.Insert(
                Math.Min(1, dictionaries.Count),
                new ResourceDictionary { Source = language.ResourceUri });
        }

        CurrentLanguageId = language.Id;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language.Id);
        return language.Id;
    }

    public string GetString(string resourceKey) =>
        Application.Current.TryFindResource(resourceKey) as string ?? resourceKey;

    public string Format(string resourceKey, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, GetString(resourceKey), arguments);

    private bool IsLanguageResource(Uri resourceUri) => _availableLanguages.Any(language =>
        resourceUri.OriginalString.EndsWith(
            GetResourceFileName(language.ResourceUri),
            StringComparison.OrdinalIgnoreCase));

    private static string GetResourceFileName(Uri resourceUri) =>
        resourceUri.OriginalString[(resourceUri.OriginalString.LastIndexOf('/') + 1)..];

    private static LanguageDefinition Create(string id, string displayNameResourceKey, string fileName) =>
        new(
            id,
            displayNameResourceKey,
            new Uri($"/SwitchBoard;component/Localization/{fileName}", UriKind.Relative));
}
