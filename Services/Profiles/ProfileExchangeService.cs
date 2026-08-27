using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SwitchBoard.Models.Profiles;

namespace SwitchBoard.Services.Profiles;

public sealed class ProfileExchangeService
{
    private readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public ProfileExchangeService() => _options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

    public async Task ExportAsync(ProfileDefinition profile, string path, CancellationToken cancellationToken = default)
    {
        var document = new ProfileExchangeDocument
        {
            ExportedAtUtc = DateTimeOffset.UtcNow,
            Profile = profile
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(document, _options), cancellationToken);
    }

    public async Task<ProfileDefinition> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        ProfileExchangeDocument? document;
        try { document = JsonSerializer.Deserialize<ProfileExchangeDocument>(json, _options); }
        catch (JsonException exception) { throw new InvalidDataException("The profile package is malformed.", exception); }
        if (document is null || document.Format != "SwitchBoard.Profile" || document.FormatVersion != 1 || document.Profile is null)
            throw new InvalidDataException("The profile package format is unsupported.");

        var imported = document.Profile;
        ProfileIdentityNormalizer.AssignNewProfileAndActionIds(imported);
        imported.CategoryId = Guid.Empty;
        ProfileCatalogService.ValidateProfileActions(imported);
        return imported;
    }

    public ProfileDefinition CloneForDuplicate(ProfileDefinition profile)
    {
        var clone = JsonSerializer.Deserialize<ProfileDefinition>(JsonSerializer.Serialize(profile, _options), _options)
                    ?? throw new InvalidDataException("The profile could not be duplicated.");
        ProfileIdentityNormalizer.AssignNewProfileAndActionIds(clone);
        return clone;
    }
}

public sealed class ProfileExchangeDocument
{
    public string Format { get; set; } = "SwitchBoard.Profile";
    public int FormatVersion { get; set; } = 1;
    public DateTimeOffset ExportedAtUtc { get; set; }
    public string ApplicationVersion { get; set; } = "1";
    public ProfileDefinition? Profile { get; set; }
}
