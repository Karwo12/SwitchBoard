using SwitchBoard.Models.Actions;
using SwitchBoard.Models;
using SwitchBoard.Services.Actions;
using SwitchBoard.ViewModels;

namespace SwitchBoard.Services.Profiles;

/// <summary>
/// Presents results already calculated by ActionItemViewModel validation before
/// a profile run. It does not create a second validation system.
/// </summary>
public sealed class ProfilePreflightService
{
    public ProfilePreflightResult Analyze(ProfileItemViewModel profile, bool profileReferencesAreValid)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var issues = new List<ProfilePreflightIssue>();
        var administratorActions = new List<string>();
        var ready = 0;

        foreach (var action in Enumerate(profile.Actions))
        {
            if (action.IsComment) continue;
            if (!action.IsEnabled) continue;
            if (action.ValidationLevel == ValidationSeverity.Error)
            {
                issues.Add(new ProfilePreflightIssue(action.DisplayName, action.ValidationMessage,
                    ProfilePreflightIssueLevel.Error));
                continue;
            }

            if (action.ValidationLevel == ValidationSeverity.Warning)
                issues.Add(new ProfilePreflightIssue(action.DisplayName, action.ValidationMessage,
                    ProfilePreflightIssueLevel.Warning));
            if (RequiresAdministrator(action)) administratorActions.Add(action.DisplayName);
            ready++;
        }

        if (!profileReferencesAreValid)
            issues.Add(new ProfilePreflightIssue(profile.Name, "Validation.ProfileReferenceCycle",
                ProfilePreflightIssueLevel.Error, true));
        return new ProfilePreflightResult(ready, issues,
            administratorActions.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static IEnumerable<ActionItemViewModel> Enumerate(IEnumerable<ActionItemViewModel> actions)
    {
        foreach (var action in actions)
        {
            // Disabled composite actions do not execute either branch. Do not
            // report validation or administrator requirements for descendants
            // that the runner will never reach.
            if (!action.IsEnabled || action.IsComment) continue;
            yield return action;
            foreach (var nested in Enumerate(action.ThenActions)) yield return nested;
            foreach (var nested in Enumerate(action.ElseActions)) yield return nested;
        }
    }

    private static bool RequiresAdministrator(ActionItemViewModel action)
    {
        var requirement = ActionDescriptorRegistry.Get(action.Type)?.AdministratorRequirement ??
            ActionAdministratorRequirement.None;
        return requirement switch
        {
            ActionAdministratorRequirement.WhenRequested =>
                action.Parameters[ActionParameterNames.RunAsAdministrator]?.GetValue<bool>() == true ||
                (action.IsRestoreScriptEnabled &&
                 action.Parameters[ActionParameterNames.RestoreScriptRunAsAdministrator]?.GetValue<bool>() == true),
            ActionAdministratorRequirement.MayRequire => true,
            _ => false
        };
    }
}

public enum ProfilePreflightIssueLevel { Warning, Error }

public sealed record ProfilePreflightIssue(string ActionName, string Message,
    ProfilePreflightIssueLevel Level, bool IsResourceKey = false);

public sealed class ProfilePreflightResult
{
    public ProfilePreflightResult(int readyActionCount, IReadOnlyList<ProfilePreflightIssue> issues,
        IReadOnlyList<string> administratorActions)
    {
        ReadyActionCount = readyActionCount;
        Issues = issues;
        AdministratorActions = administratorActions;
    }

    public int ReadyActionCount { get; }
    public IReadOnlyList<ProfilePreflightIssue> Issues { get; }
    public IReadOnlyList<string> AdministratorActions { get; }
    public int ErrorCount => Issues.Count(issue => issue.Level == ProfilePreflightIssueLevel.Error);
    public int WarningCount => Issues.Count(issue => issue.Level == ProfilePreflightIssueLevel.Warning);
    public bool HasErrors => ErrorCount > 0;
    public bool RequiresAdministrator => AdministratorActions.Count > 0;
}
