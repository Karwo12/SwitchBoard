using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using SwitchBoard.Models.Actions;
using SwitchBoard.Models.Profiles;

namespace SwitchBoard.Services.Profiles;

/// <summary>
/// Assigns fresh durable identities to copied/imported profile data. Runtime and
/// restore state are intentionally absent from these persistence models.
/// </summary>
public static class ProfileIdentityNormalizer
{
    public static void AssignNewProfileAndActionIds(ProfileDefinition profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Id = Guid.NewGuid();
        AssignNewActionIds(profile.Actions);
    }

    public static void AssignNewActionIds(IEnumerable<ActionDefinition> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        foreach (var action in actions)
        {
            if (action is null) throw new InvalidDataException("A copied profile contains an invalid action.");
            AssignNewActionIds(action);
        }
    }

    public static void AssignNewActionIds(ActionDefinition action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action.Id = Guid.NewGuid();
        if (action.Parameters is null) return;

        foreach (var property in new[] { ActionParameterNames.ThenActions, ActionParameterNames.ElseActions })
        {
            if (action.Parameters[property] is not JsonArray nested) continue;
            for (var index = 0; index < nested.Count; index++)
            {
                ActionDefinition? child;
                try { child = ActionDefinitionJson.Deserialize(nested[index]); }
                catch (Exception exception) when (exception is JsonException or InvalidOperationException or NotSupportedException)
                {
                    throw new InvalidDataException($"The nested action branch '{property}' contains malformed action data.",
                        exception);
                }
                if (child is null)
                    throw new InvalidDataException($"The nested action branch '{property}' contains an empty action.");
                AssignNewActionIds(child);
                nested[index] = ActionDefinitionJson.Serialize(child);
            }
        }
    }
}
