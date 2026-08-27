using System.Text.Json;
using System.Text.Json.Nodes;
using SwitchBoard.Models.Actions;
using SwitchBoard.Models.Profiles;

namespace SwitchBoard.Services.Profiles;

/// <summary>
/// Validates profile.run references, including references inside condition branches.
/// Keeping graph traversal outside the window VM also makes the nested-action rule
/// reusable by catalog and execution-facing code.
/// </summary>
public static class ProfileReferenceValidator
{
    public static bool AreValid(IEnumerable<ProfileDefinition> source, Guid rootProfileId)
    {
        ArgumentNullException.ThrowIfNull(source);
        var profiles = new Dictionary<Guid, ProfileDefinition>();
        foreach (var profile in source)
        {
            if (profile is null || profile.Id == Guid.Empty || !profiles.TryAdd(profile.Id, profile)) return false;
        }
        var visiting = new HashSet<Guid>();
        var visited = new HashSet<Guid>();
        return Visit(rootProfileId);

        bool Visit(Guid id)
        {
            if (!profiles.TryGetValue(id, out var profile)) return false;
            if (visited.Contains(id)) return true;
            if (!visiting.Add(id)) return false;
            if (profile.Actions is null || !TryGetProfileTargets(profile.Actions, out var targets)) return false;
            foreach (var target in targets)
                if (!Visit(target)) return false;
            visiting.Remove(id);
            visited.Add(id);
            return true;
        }
    }

    private static bool TryGetProfileTargets(IEnumerable<ActionDefinition> actions, out IReadOnlyList<Guid> targets)
    {
        var result = new List<Guid>();
        foreach (var action in actions)
        {
            if (action is null || action.Parameters is null)
            {
                targets = [];
                return false;
            }
            // Disabled actions (including composite branches) are never executed and
            // therefore must not introduce a false cycle or a missing-profile block.
            if (!action.IsEnabled) continue;
            if (action.Type == ActionTypeIds.ProfileRun)
            {
                try
                {
                    if (!Guid.TryParse(action.Parameters[ActionParameterNames.ProfileId]?.GetValue<string>(), out var id))
                    {
                        targets = [];
                        return false;
                    }
                    result.Add(id);
                }
                catch (InvalidOperationException)
                {
                    targets = [];
                    return false;
                }
            }
            if (action.Type != ActionTypeIds.ConditionIf) continue;
            foreach (var name in new[] { ActionParameterNames.ThenActions, ActionParameterNames.ElseActions })
            {
                if (!action.Parameters.TryGetPropertyValue(name, out var branch)) continue;
                if (branch is not JsonArray array)
                {
                    targets = [];
                    return false;
                }
                var nested = new List<ActionDefinition>();
                foreach (var node in array)
                {
                    try
                    {
                        if (ActionDefinitionJson.Deserialize(node) is not { } child)
                        {
                            targets = [];
                            return false;
                        }
                        nested.Add(child);
                    }
                    catch (Exception exception) when (exception is JsonException or InvalidOperationException or NotSupportedException)
                    {
                        targets = [];
                        return false;
                    }
                }
                if (!TryGetProfileTargets(nested, out var nestedTargets))
                {
                    targets = [];
                    return false;
                }
                result.AddRange(nestedTargets);
            }
        }
        targets = result;
        return true;
    }
}
