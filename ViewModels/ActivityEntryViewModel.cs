using SwitchBoard.Services.Activity;

namespace SwitchBoard.ViewModels;

public sealed class ActivityEntryViewModel
{
    public ActivityEntryViewModel(ActivityEntry entry, string? profileName, string? actionName)
    {
        Timestamp = entry.Timestamp;
        Level = entry.Level;
        ProfileId = entry.ProfileId;
        ActionId = entry.ActionId;
        var target = entry.ActionId is not null ? actionName : profileName;
        if (!string.IsNullOrWhiteSpace(target) && entry.Message.EndsWith(target, StringComparison.CurrentCultureIgnoreCase))
        {
            TargetName = target;
            Prefix = entry.Message[..^target.Length].TrimEnd();
        }
        else Prefix = entry.Message;
    }

    public DateTimeOffset Timestamp { get; }
    public ActivityLevel Level { get; }
    public Guid? ProfileId { get; }
    public Guid? ActionId { get; }
    public string Prefix { get; }
    public string? TargetName { get; }
    public bool IsNavigable => ProfileId is not null && TargetName is not null;
}
