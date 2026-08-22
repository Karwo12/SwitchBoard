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
        var profiles = source.ToDictionary(profile => profile.Id);
        var visiting = new HashSet<Guid>();
        var visited = new HashSet<Guid>();
        return Visit(rootProfileId);

        bool Visit(Guid id)
        {
            if (!profiles.TryGetValue(id, out var profile)) return false;
            if (visited.Contains(id)) return true;
            if (!visiting.Add(id)) return false;
            foreach (var target in profile.Actions.SelectMany(EnumerateProfileTargets))
                if (!Visit(target)) return false;
            visiting.Remove(id);
            visited.Add(id);
            return true;
        }
    }

    private static IEnumerable<Guid> EnumerateProfileTargets(ActionDefinition action)
    {
        if (action.Type == ActionTypeIds.ProfileRun &&
            Guid.TryParse(action.Parameters[ActionParameterNames.ProfileId]?.GetValue<string>(), out var id))
            yield return id;
        if (action.Type != ActionTypeIds.ConditionIf) yield break;
        foreach (var name in new[] { ActionParameterNames.ThenActions, ActionParameterNames.ElseActions })
        {
            if (action.Parameters[name] is not JsonArray array) continue;
            foreach (var node in array)
            {
                ActionDefinition? nested = null;
                try { nested = node?.Deserialize<ActionDefinition>(); } catch (JsonException) { }
                if (nested is null) continue;
                foreach (var target in EnumerateProfileTargets(nested)) yield return target;
            }
        }
    }
}
