using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using SwitchBoard.Models.Actions;
using SwitchBoard.Models.Profiles;

namespace SwitchBoard.Services.Profiles;

public static class ProfileRuntimeValidator
{
    public static ProfileRuntimeValidationResult Validate(ProfileDefinition profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var errors = new List<string>();
        var warnings = new List<string>();
        var diagnostics = new List<ProfileActionDiagnostic>();
        var identities = new HashSet<Guid>();
        var actionReferences = new HashSet<ActionDefinition>(ReferenceEqualityComparer.Instance);
        var parameterReferences = new HashSet<JsonObject>(ReferenceEqualityComparer.Instance);
        var index = 0;

        if (profile.Id == Guid.Empty) errors.Add("The profile has an empty identifier.");
        if (profile.Actions is null)
        {
            errors.Add("The profile has no action collection.");
            return new ProfileRuntimeValidationResult(errors, warnings, diagnostics);
        }

        ValidateActions(profile.Actions, "actions", 0);
        return new ProfileRuntimeValidationResult(errors, warnings, diagnostics);

        void ValidateActions(IEnumerable<ActionDefinition> actions, string path, int depth)
        {
            var sortOrders = new HashSet<int>();
            var localIndex = 0;
            foreach (var action in actions)
            {
                var location = $"{path}[{localIndex++}]";
                index++;
                if (action is null)
                {
                    errors.Add($"{location} is null.");
                    continue;
                }

                if (!actionReferences.Add(action))
                    errors.Add($"{location} shares the same mutable action instance with another entry.");
                if (action.Id == Guid.Empty)
                    errors.Add($"{location} has an empty action identifier.");
                else if (!identities.Add(action.Id))
                    errors.Add($"{location} has duplicate action identifier {action.Id}.");
                if (string.IsNullOrWhiteSpace(action.Type)) errors.Add($"{location} has no ActionTypeId.");
                if (!sortOrders.Add(action.SortOrder))
                    warnings.Add($"{path} contains duplicate SortOrder {action.SortOrder}; list order is used as a tie-breaker.");
                if (action.Parameters is null)
                {
                    errors.Add($"{location} has null parameters.");
                    diagnostics.Add(new(index, location, action.Id, action.Type, action.Name, action.IsEnabled,
                        action.SortOrder, action.GetType().FullName ?? action.GetType().Name, null));
                    continue;
                }
                if (!parameterReferences.Add(action.Parameters))
                    errors.Add($"{location} shares the same mutable Parameters object with another action.");

                diagnostics.Add(new(index, location, action.Id, action.Type, action.Name, action.IsEnabled,
                    action.SortOrder, action.GetType().FullName ?? action.GetType().Name, action.Parameters));
                if (depth >= Services.Execution.ProfileRunner.MaximumNestingDepth)
                {
                    if (HasNestedData(action)) errors.Add($"{location} exceeds the maximum nesting depth.");
                    continue;
                }

                foreach (var property in new[] { ActionParameterNames.ThenActions, ActionParameterNames.ElseActions })
                {
                    if (!action.Parameters.TryGetPropertyValue(property, out var branch)) continue;
                    if (branch is not JsonArray array)
                    {
                        errors.Add($"{location}/{property} is malformed.");
                        continue;
                    }

                    var nested = new List<ActionDefinition>();
                    foreach (var node in array)
                    {
                        try
                        {
                            var child = ActionDefinitionJson.Deserialize(node);
                            if (child is null) errors.Add($"{location}/{property} contains an empty action.");
                            else nested.Add(child);
                        }
                        catch (Exception exception) when (exception is JsonException or InvalidOperationException or NotSupportedException)
                        {
                            errors.Add($"{location}/{property} contains malformed action data: {exception.Message}");
                        }
                    }
                    ValidateActions(nested, $"{location}/{property}", depth + 1);
                }
            }
        }
    }

    private static bool HasNestedData(ActionDefinition action) =>
        action.Parameters[ActionParameterNames.ThenActions] is JsonArray { Count: > 0 } ||
        action.Parameters[ActionParameterNames.ElseActions] is JsonArray { Count: > 0 };
}

public sealed record ProfileActionDiagnostic(int Index, string Location, Guid Id, string ActionTypeId, string? Name,
    bool Enabled, int SortOrder, string RuntimeModelType, JsonObject? Parameters);

public sealed record ProfileRuntimeValidationResult(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings,
    IReadOnlyList<ProfileActionDiagnostic> Actions)
{
    public bool IsValid => Errors.Count == 0;
}
