using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SwitchBoard.Models.Actions;
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
        imported.Id = Guid.NewGuid();
        imported.CategoryId = Guid.Empty;
        foreach (var action in imported.Actions.OrderBy(item => item.SortOrder)) ResetIds(action);
        return imported;
    }

    private static void ResetIds(ActionDefinition action)
    {
        action.Id = Guid.NewGuid();
        action.RuntimeProcessIdHint = null;
        foreach (var key in new[] { ActionParameterNames.ThenActions, ActionParameterNames.ElseActions })
        {
            if (action.Parameters[key] is not JsonArray nested) continue;
            for (var index = 0; index < nested.Count; index++)
            {
                if (nested[index]?.Deserialize<ActionDefinition>(new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
                    }) is not { } child) continue;
                ResetIds(child);
                nested[index] = JsonSerializer.SerializeToNode(child, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
                });
            }
        }
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
