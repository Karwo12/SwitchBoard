using SwitchBoard.Data;
using SwitchBoard.Services.Persistence;
using SwitchBoard.Models.Actions;
using System.Text.Json;
using System.Text.Json.Nodes;
using SwitchBoard.Services.Execution;

namespace SwitchBoard.Services.Profiles;

public sealed class ProfileCatalogService(ICatalogRepository repository) : IProfileCatalogService
{
    public Task<SwitchBoardCatalog> LoadAsync(CancellationToken cancellationToken = default) =>
        repository.LoadAsync(cancellationToken);

    public Task SaveAsync(SwitchBoardCatalog catalog, CancellationToken cancellationToken = default)
    {
        Validate(catalog);
        catalog.SchemaVersion = CatalogSchema.CurrentVersion;
        return repository.SaveAsync(catalog, cancellationToken);
    }

    private static void Validate(SwitchBoardCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        if (catalog.Categories.Any(category => category.Id == Guid.Empty))
        {
            throw new InvalidOperationException("Every category must have a valid identifier.");
        }

        if (catalog.Profiles.Any(profile => profile.Id == Guid.Empty))
        {
            throw new InvalidOperationException("Every profile must have a valid identifier.");
        }

        if (catalog.Categories.Any(category => string.IsNullOrWhiteSpace(category.Name)))
        {
            throw new InvalidOperationException("Category names cannot be empty.");
        }

        if (catalog.Profiles.Any(profile => string.IsNullOrWhiteSpace(profile.Name)))
        {
            throw new InvalidOperationException("Profile names cannot be empty.");
        }

        if (catalog.Categories.Select(category => category.Id).Distinct().Count() != catalog.Categories.Count)
        {
            throw new InvalidOperationException("Category identifiers must be unique.");
        }

        if (catalog.Profiles.Select(profile => profile.Id).Distinct().Count() != catalog.Profiles.Count)
        {
            throw new InvalidOperationException("Profile identifiers must be unique.");
        }

        var categoryIds = catalog.Categories.Select(category => category.Id).ToHashSet();
        if (catalog.Profiles.Any(profile => !categoryIds.Contains(profile.CategoryId)))
        {
            throw new InvalidOperationException("Every profile must belong to an existing category.");
        }

        if (catalog.Profiles.SelectMany(profile => profile.Actions).Any(action =>
                action.Id == Guid.Empty || string.IsNullOrWhiteSpace(action.Type)))
        {
            throw new InvalidOperationException("Every action must have an identifier and a type.");
        }

        var profileIds = catalog.Profiles.Select(profile => profile.Id).ToHashSet();
        foreach (var profile in catalog.Profiles)
            foreach (var action in profile.Actions)
                ValidateAction(action, 0, profileIds);

        var edges = catalog.Profiles.ToDictionary(profile => profile.Id,
            profile => profile.Actions.SelectMany(EnumerateProfileTargets).ToHashSet());
        var visiting = new HashSet<Guid>();
        var visited = new HashSet<Guid>();
        foreach (var profile in catalog.Profiles)
            if (HasCycle(profile.Id, edges, visiting, visited))
                throw new InvalidOperationException("Profile dependencies contain a cycle.");
    }

    private static void ValidateAction(ActionDefinition action, int depth, IReadOnlySet<Guid> profileIds)
    {
        if (depth > ProfileRunner.MaximumNestingDepth)
            throw new InvalidOperationException($"Automation nesting exceeds {ProfileRunner.MaximumNestingDepth} levels.");
        if (action.Type == ActionTypeIds.ProfileRun)
        {
            var value = action.Parameters[ActionParameterNames.ProfileId]?.GetValue<string>();
            if (!Guid.TryParse(value, out var target) || !profileIds.Contains(target))
                throw new InvalidOperationException("A Run another profile action points to a missing profile.");
        }
        foreach (var nested in EnumerateNested(action)) ValidateAction(nested, depth + 1, profileIds);
    }

    private static IEnumerable<Guid> EnumerateProfileTargets(ActionDefinition action)
    {
        if (action.Type == ActionTypeIds.ProfileRun &&
            Guid.TryParse(action.Parameters[ActionParameterNames.ProfileId]?.GetValue<string>(), out var id)) yield return id;
        foreach (var nested in EnumerateNested(action))
            foreach (var target in EnumerateProfileTargets(nested)) yield return target;
    }

    private static IEnumerable<ActionDefinition> EnumerateNested(ActionDefinition action)
    {
        if (action.Type != ActionTypeIds.ConditionIf) yield break;
        foreach (var name in new[] { ActionParameterNames.ThenActions, ActionParameterNames.ElseActions })
            if (action.Parameters[name] is JsonArray array)
                foreach (var node in array)
                {
                    ActionDefinition? nested = null;
                    try { nested = node?.Deserialize<ActionDefinition>(); } catch (JsonException) { }
                    if (nested is not null) yield return nested;
                }
    }

    private static bool HasCycle(Guid id, IReadOnlyDictionary<Guid, HashSet<Guid>> edges,
        HashSet<Guid> visiting, HashSet<Guid> visited)
    {
        if (visited.Contains(id)) return false;
        if (!visiting.Add(id)) return true;
        if (edges.TryGetValue(id, out var targets))
            foreach (var target in targets)
                if (HasCycle(target, edges, visiting, visited)) return true;
        visiting.Remove(id);
        visited.Add(id);
        return false;
    }
}
