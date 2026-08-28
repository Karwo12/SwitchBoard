using SwitchBoard.Services.Activity;
using SwitchBoard.Localization;

namespace SwitchBoard.ViewModels;

public sealed class ActivityEntryViewModel
{
    public ActivityEntryViewModel(ActivityEntry entry, string? profileName, string? actionName,
        ILocalizationService localization)
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
        SourceText = TargetName ?? profileName ?? localization.GetString("Activity.Source.SwitchBoard");
        Description = RemoveStatusPrefix(entry.Message, TargetName);
        StatusText = localization.GetString(entry.Level switch
        {
            ActivityLevel.Success => "NotificationLevel.Success",
            ActivityLevel.Warning => "NotificationLevel.Warning",
            ActivityLevel.Error => "NotificationLevel.Error",
            _ => "NotificationLevel.Info"
        });
    }

    public DateTimeOffset Timestamp { get; }
    public ActivityLevel Level { get; }
    public Guid? ProfileId { get; }
    public Guid? ActionId { get; }
    public string Prefix { get; }
    public string? TargetName { get; }
    public string SourceText { get; }
    public string Description { get; }
    public string StatusText { get; }
    public bool IsNavigable => ProfileId is not null && TargetName is not null;

    private static string RemoveStatusPrefix(string message, string? target)
    {
        var result = message.Trim();
        var separator = result.IndexOf(':');
        if (separator is >= 0 and <= 18)
            result = result[(separator + 1)..].Trim();
        if (!string.IsNullOrWhiteSpace(target) && result.StartsWith(target, StringComparison.CurrentCultureIgnoreCase))
        {
            result = result[target.Length..].TrimStart();
            if (result.StartsWith('—')) result = result[1..].Trim();
        }
        return result;
    }
}
