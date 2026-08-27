using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SwitchBoard.Models.Actions;

/// <summary>Canonical JSON contract for action definitions embedded in JsonObject parameters.</summary>
public static class ActionDefinitionJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static ActionDefinition? Deserialize(JsonNode? node) => node?.Deserialize<ActionDefinition>(Options);

    public static JsonNode? Serialize(ActionDefinition action) => JsonSerializer.SerializeToNode(action, Options);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
