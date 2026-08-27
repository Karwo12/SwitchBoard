using SwitchBoard.Data;
using SwitchBoard.Services.Persistence;
using SwitchBoard.Models.Actions;
using SwitchBoard.Models.Profiles;
using System.IO;
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
        ValidateForImport(catalog);
        catalog.SchemaVersion = CatalogSchema.CurrentVersion;
        return repository.SaveAsync(catalog, cancellationToken);
    }

    public static void ValidateForImport(SwitchBoardCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        catalog.Categories ??= [];
        catalog.Profiles ??= [];

        if (catalog.SchemaVersion > CatalogSchema.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Catalog schema {catalog.SchemaVersion} is newer than supported schema {CatalogSchema.CurrentVersion}.");
        }

        if (catalog.Categories.Any(category => category is null || category.Id == Guid.Empty))
        {
            throw new InvalidOperationException("Every category must have a valid identifier.");
        }

        if (catalog.Profiles.Any(profile => profile is null || profile.Id == Guid.Empty))
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
        if (catalog.Profiles.Any(profile => profile.CategoryId != Guid.Empty && !categoryIds.Contains(profile.CategoryId)))
        {
            throw new InvalidOperationException("Every profile must belong to an existing category or the root.");
        }

        if (catalog.RootNavigationOrder is not null)
        {
            var rootProfileIds = catalog.Profiles.Where(profile => profile.CategoryId == Guid.Empty)
                .Select(profile => profile.Id).ToHashSet();
            var seenCategories = new HashSet<Guid>();
            var seenProfiles = new HashSet<Guid>();
            foreach (var entry in catalog.RootNavigationOrder)
            {
                var isValid = entry.Kind switch
                {
                    RootNavigationItemKind.Category => categoryIds.Contains(entry.Id) && seenCategories.Add(entry.Id),
                    RootNavigationItemKind.Profile => rootProfileIds.Contains(entry.Id) && seenProfiles.Add(entry.Id),
                    _ => false
                };
                if (!isValid)
                    throw new InvalidOperationException("Root navigation order contains an invalid or duplicate entry.");
            }
        }

        var profileIds = catalog.Profiles.Select(profile => profile.Id).ToHashSet();
        var actionIds = new HashSet<Guid>();
        foreach (var profile in catalog.Profiles)
            ValidateProfileActions(profile, profileIds, actionIds);

        var edges = catalog.Profiles.ToDictionary(profile => profile.Id,
            profile => profile.Actions.SelectMany(EnumerateProfileTargets).ToHashSet());
        var visiting = new HashSet<Guid>();
        var visited = new HashSet<Guid>();
        foreach (var profile in catalog.Profiles)
            if (HasCycle(profile.Id, edges, visiting, visited))
                throw new InvalidOperationException("Profile dependencies contain a cycle.");
    }

    internal static void ValidateProfileActions(ProfileDefinition profile, IReadOnlySet<Guid>? profileIds = null,
        HashSet<Guid>? actionIds = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Actions is null)
            throw new InvalidDataException("A profile contains a missing action list.");
        actionIds ??= [];
        foreach (var action in profile.Actions)
            ValidateAction(action, 0, profileIds, actionIds);
    }

    private static void ValidateAction(ActionDefinition? action, int depth, IReadOnlySet<Guid>? profileIds,
        HashSet<Guid> actionIds)
    {
        if (action is null)
            throw new InvalidDataException("A profile contains an invalid action.");
        if (action.Id == Guid.Empty || string.IsNullOrWhiteSpace(action.Type))
            throw new InvalidOperationException("Every action must have an identifier and a type.");
        if (!actionIds.Add(action.Id))
            throw new InvalidOperationException($"Action identifier {action.Id} is duplicated.");
        if (action.Parameters is null)
            throw new InvalidDataException("An action contains missing parameters.");
        if (depth > ProfileRunner.MaximumNestingDepth)
            throw new InvalidOperationException($"Automation nesting exceeds {ProfileRunner.MaximumNestingDepth} levels.");
        if (action.Type == ActionTypeIds.ProfileRun && profileIds is not null)
        {
            var value = action.Parameters[ActionParameterNames.ProfileId]?.GetValue<string>();
            if (!Guid.TryParse(value, out var target) || !profileIds.Contains(target))
                throw new InvalidOperationException("A Run another profile action points to a missing profile.");
        }
        foreach (var nested in EnumerateNested(action)) ValidateAction(nested, depth + 1, profileIds, actionIds);
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
        {
            if (!action.Parameters.TryGetPropertyValue(name, out var branch)) continue;
            if (branch is not JsonArray array)
                throw new InvalidDataException($"The nested action branch '{name}' is malformed.");
            foreach (var node in array)
            {
                if (node is null)
                    throw new InvalidDataException($"The nested action branch '{name}' contains an empty action.");
                ActionDefinition? nested;
                try { nested = ActionDefinitionJson.Deserialize(node); }
                catch (Exception exception) when (exception is JsonException or InvalidOperationException or NotSupportedException)
                {
                    throw new InvalidDataException($"The nested action branch '{name}' contains malformed action data.",
                        exception);
                }
                if (nested is null)
                    throw new InvalidDataException($"The nested action branch '{name}' contains an empty action.");
                yield return nested;
            }
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
