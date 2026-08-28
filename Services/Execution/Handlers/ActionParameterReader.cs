using System.Text.Json.Nodes;

namespace SwitchBoard.Services.Execution.Handlers;

internal static class ActionParameterReader
{
    public static string ReadString(JsonObject parameters, string propertyName) =>
        TryGetValue<string>(parameters, propertyName) ?? string.Empty;

    public static bool ReadBoolean(JsonObject parameters, string propertyName, bool defaultValue) =>
        TryGetValue<bool?>(parameters, propertyName) ?? defaultValue;

    public static int ReadInt32(JsonObject parameters, string propertyName, int defaultValue) =>
        TryGetValue<int?>(parameters, propertyName) ?? defaultValue;

    private static T? TryGetValue<T>(JsonObject parameters, string propertyName)
    {
        try
        {
            return parameters[propertyName] is JsonNode node ? node.GetValue<T>() : default;
        }
        catch (InvalidOperationException)
        {
            return default;
        }
        catch (FormatException)
        {
            return default;
        }
    }
}