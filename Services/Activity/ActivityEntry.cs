namespace SwitchBoard.Services.Activity;

public sealed record ActivityEntry(
    DateTimeOffset Timestamp,
    ActivityLevel Level,
    string Message,
    Guid? ProfileId = null,
    Guid? ActionId = null);
