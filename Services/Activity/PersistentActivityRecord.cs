using System.Text.Json.Nodes;

namespace SwitchBoard.Services.Activity;

public sealed class PersistentActivityRecord
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public Guid? SessionId { get; set; }
    public Guid? ProfileId { get; set; }
    public string? ProfileName { get; set; }
    public Guid? ActionId { get; set; }
    public string? ActionType { get; set; }
    public string? FriendlyName { get; set; }
    public string EventType { get; set; } = ActivityEventTypes.Activity;
    public ActivityLevel Level { get; set; }
    public JsonObject? StateBefore { get; set; }
    public JsonObject? RequestedState { get; set; }
    public JsonObject? StateAfter { get; set; }
    public string? Result { get; set; }
    public string? RestoreStatus { get; set; }
    public string Message { get; set; } = string.Empty;
}

public static class ActivityEventTypes
{
    public const string Activity = "activity";
    public const string Execute = "execute";
    public const string Verify = "verify";
    public const string Restore = "restore";
    public const string Discard = "discard";
    public const string Failed = "failed";
    public const string ExternalChange = "external-change";
}

public static class SystemChangeStatuses
{
    public const string Pending = "pending";
    public const string Restored = "restored";
    public const string Discarded = "discarded";
    public const string RestoreFailed = "restore-failed";
    public const string ExternalChange = "external-change";
    public const string LeftActive = "left-active";
}

public sealed record SystemChangeEntry(
    DateTimeOffset Timestamp,
    Guid SessionId,
    Guid ActionId,
    string ActionType,
    string FriendlyName,
    JsonObject? StateBefore,
    JsonObject? RequestedState,
    JsonObject? StateAfter,
    string Status,
    string Message);
