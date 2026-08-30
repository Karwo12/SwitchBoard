namespace SwitchBoard.Services.Activity;

public interface IActivityService
{
    event EventHandler<ActivityEntry>? EntryAdded;
    event EventHandler? PersistentViewsChanged;
    IReadOnlyList<ActivityEntry> Entries { get; }
    IReadOnlyList<PersistentActivityRecord> Records { get; }
    IReadOnlyList<ActivityEntry> HistoryEntries { get; }
    IReadOnlyList<SystemChangeEntry> SystemChanges { get; }
    void Add(ActivityLevel level, string message, Guid? profileId = null, Guid? actionId = null);
    void Record(PersistentActivityRecord record);
    void Clear();
    void SetRetentionDays(int retentionDays);
    void ClearPersistentHistory();
}
