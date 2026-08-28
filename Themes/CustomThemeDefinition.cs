namespace SwitchBoard.Themes;

public sealed class CustomThemeDefinition
{
    public string Id { get; set; } = CreateId();
    public string Name { get; set; } = string.Empty;
    public CustomThemeSettings Colors { get; set; } = CustomThemeSettings.CreateDefault();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool IsBuiltIn { get; set; }

    public static string CreateId() => $"custom-{Guid.NewGuid():N}";

    public CustomThemeDefinition Clone(string? newId = null) => new()
    {
        Id = newId ?? Id,
        Name = Name,
        Colors = Colors.Clone(),
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        IsBuiltIn = IsBuiltIn
    };
}
