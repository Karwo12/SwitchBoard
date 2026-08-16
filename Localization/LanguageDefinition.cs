namespace SwitchBoard.Localization;

public sealed record LanguageDefinition(
    string Id,
    string DisplayNameResourceKey,
    Uri ResourceUri);
