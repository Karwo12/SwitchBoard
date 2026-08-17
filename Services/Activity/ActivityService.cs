using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SwitchBoard.Data;
using SwitchBoard.Localization;
using SwitchBoard.Models.Actions;
using SwitchBoard.Services.Logging;
using SwitchBoard.Services.Windows;

namespace SwitchBoard.Services.Activity;

public sealed class ActivityService : IActivityService
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(90);
    private readonly object _gate = new();
    private readonly Queue<ActivityEntry> _entries = new();
    private readonly List<PersistentActivityRecord> _records = [];
    private readonly int _capacity;
    private readonly string? _logsDirectory;
    private readonly ILocalizationService? _localization;
    private readonly IAppLogger? _logger;
    private readonly JsonSerializerOptions _options = CreateOptions();
    private IReadOnlyList<ActivityEntry> _historyEntries = [];
    private IReadOnlyList<SystemChangeEntry> _systemChanges = [];

    public ActivityService(int capacity = 300) : this(null, null, null, capacity) { }

    public ActivityService(AppDataPaths? paths, ILocalizationService? localization = null,
        IAppLogger? logger = null, int capacity = 300)
    {
        _capacity = Math.Clamp(capacity, 200, 500);
        _logsDirectory = paths?.LogsDirectory;
        _localization = localization;
        _logger = logger;
        if (_logsDirectory is null) return;
        Directory.CreateDirectory(_logsDirectory);
        LoadPersistentRecords();
        ApplyRetention();
        RebuildPersistentViews();
    }

    public event EventHandler<ActivityEntry>? EntryAdded;
    public event EventHandler? PersistentViewsChanged;

    public IReadOnlyList<ActivityEntry> Entries
    {
        get { lock (_gate) return _entries.ToArray(); }
    }

    public IReadOnlyList<ActivityEntry> HistoryEntries
    {
        get { lock (_gate) return _historyEntries; }
    }

    public IReadOnlyList<SystemChangeEntry> SystemChanges
    {
        get { lock (_gate) return _systemChanges; }
    }

    public void Add(ActivityLevel level, string message, Guid? profileId = null, Guid? actionId = null) =>
        Record(new PersistentActivityRecord
        {
            Level = level,
            Message = message,
            ProfileId = profileId,
            ActionId = actionId,
            EventType = ActivityEventTypes.Activity
        });

    public void Record(PersistentActivityRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(record.Message)) return;
        record.Timestamp = record.Timestamp == default ? DateTimeOffset.UtcNow : record.Timestamp;
        record.Message = record.Message.Trim();
        var entry = new ActivityEntry(record.Timestamp.ToLocalTime(), record.Level, record.Message,
            record.ProfileId, record.ActionId);
        lock (_gate)
        {
            // JSONL append plus Flush(true) is the crash boundary: each important event is durable independently.
            if (_logsDirectory is not null)
            {
                try { AppendWriteThrough(record); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _logger?.Error("Activity", exception, "Persistent activity event could not be written.");
                }
            }
            _records.Add(Clone(record));
            _entries.Enqueue(entry);
            while (_entries.Count > _capacity) _entries.Dequeue();
            RebuildPersistentViews();
        }
        EntryAdded?.Invoke(this, entry);
        PersistentViewsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task ReconcileServiceChangesAsync(IWindowsServiceManager serviceManager,
        CancellationToken cancellationToken = default)
    {
        var candidates = SystemChanges.Where(change =>
                change.ActionType == ActionTypeIds.ServiceSetState &&
                change.Status is SystemChangeStatuses.Pending or SystemChangeStatuses.Discarded or
                    SystemChangeStatuses.LeftActive)
            .ToList();
        foreach (var change in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var serviceName = change.StateBefore?["serviceName"]?.GetValue<string>() ??
                              change.RequestedState?[ActionParameterNames.ServiceName]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(serviceName) || change.StateAfter is null) continue;
            try
            {
                var actual = await serviceManager.GetSnapshotAsync(serviceName, cancellationToken);
                var expectedRuntime = change.StateAfter["runtimeState"]?.GetValue<string>();
                var expectedStartup = change.StateAfter["startupType"]?.GetValue<string>();
                if (MatchesRuntime(actual.RuntimeState, expectedRuntime) &&
                    MatchesStartup(actual.StartupType, expectedStartup)) continue;
                Record(new PersistentActivityRecord
                {
                    SessionId = change.SessionId,
                    ActionId = change.ActionId,
                    ActionType = change.ActionType,
                    FriendlyName = change.FriendlyName,
                    EventType = ActivityEventTypes.ExternalChange,
                    Level = ActivityLevel.Warning,
                    StateAfter = new JsonObject
                    {
                        ["runtimeState"] = RuntimeId(actual.RuntimeState),
                        ["startupType"] = StartupId(actual.StartupType)
                    },
                    RestoreStatus = SystemChangeStatuses.ExternalChange,
                    Message = _localization?.Format("Activity.ExternalChange", change.FriendlyName) ??
                              $"{change.FriendlyName}: system state was changed after SwitchBoard's action."
                });
            }
            catch
            {
                // Reconciliation is best effort and never controls a service.
            }
        }
    }

    public void Clear()
    {
        lock (_gate) _entries.Clear();
    }

    private void AppendWriteThrough(PersistentActivityRecord record)
    {
        var path = Path.Combine(_logsDirectory!, $"activity-{record.Timestamp.ToLocalTime():yyyy-MM-dd}.jsonl");
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, _options) + Environment.NewLine);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
            4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(true);
    }

    private void LoadPersistentRecords()
    {
        foreach (var path in Directory.EnumerateFiles(_logsDirectory!, "activity-*.jsonl")
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                foreach (var line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        if (JsonSerializer.Deserialize<PersistentActivityRecord>(line, _options) is { } record)
                            _records.Add(record);
                    }
                    catch (JsonException) { }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger?.Error("Activity", exception, $"Persistent activity file '{Path.GetFileName(path)}' could not be read.");
            }
        }
    }

    private void ApplyRetention()
    {
        var latestStatuses = _records.Where(IsSystemChangeRecord)
            .GroupBy(ChangeKey)
            .ToDictionary(group => group.Key,
                group => group.OrderBy(item => item.Timestamp).Last().RestoreStatus,
                StringComparer.Ordinal);
        var protectedKeys = latestStatuses.Where(item => item.Value is SystemChangeStatuses.Pending or
                SystemChangeStatuses.Discarded or SystemChangeStatuses.LeftActive or
                SystemChangeStatuses.RestoreFailed)
            .Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        var cutoff = DateTimeOffset.UtcNow - Retention;
        foreach (var path in Directory.EnumerateFiles(_logsDirectory!, "activity-*.jsonl").ToList())
        {
            var info = new FileInfo(path);
            var fileDate = TryGetActivityFileDate(path) ?? new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            if (fileDate >= cutoff) continue;
            var protectsUnresolved = _records.Any(record => IsSystemChangeRecord(record) &&
                protectedKeys.Contains(ChangeKey(record)) && RecordBelongsToFile(record, path));
            if (!protectsUnresolved)
            {
                try { File.Delete(path); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _logger?.Error("Activity", exception,
                        $"Expired activity file '{Path.GetFileName(path)}' could not be removed.");
                }
            }
        }
        _records.RemoveAll(record => record.Timestamp < cutoff &&
            (!IsSystemChangeRecord(record) || !protectedKeys.Contains(ChangeKey(record))));
    }

    private static DateTimeOffset? TryGetActivityFileDate(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (!name.StartsWith("activity-", StringComparison.OrdinalIgnoreCase)) return null;
        return DateTime.TryParseExact(name["activity-".Length..], "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeLocal, out var value)
            ? new DateTimeOffset(value)
            : null;
    }

    private void RebuildPersistentViews()
    {
        _historyEntries = _records.OrderByDescending(record => record.Timestamp)
            .Take(1000)
            .Select(record => new ActivityEntry(record.Timestamp.ToLocalTime(), record.Level, record.Message,
                record.ProfileId, record.ActionId))
            .ToList();
        _systemChanges = _records.Where(IsSystemChangeRecord)
            .Where(record => record.SessionId.HasValue && record.ActionId.HasValue)
            .GroupBy(ChangeKey)
            .Select(group =>
            {
                var ordered = group.OrderBy(item => item.Timestamp).ToList();
                var first = ordered.FirstOrDefault(item => item.StateBefore is not null || item.RequestedState is not null)
                            ?? ordered[0];
                var last = ordered[^1];
                return new SystemChangeEntry(first.Timestamp.ToLocalTime(), first.SessionId!.Value,
                    first.ActionId!.Value, first.ActionType ?? string.Empty,
                    first.FriendlyName ?? first.ActionType ?? string.Empty,
                    first.StateBefore?.DeepClone().AsObject(), first.RequestedState?.DeepClone().AsObject(),
                    first.StateAfter?.DeepClone().AsObject(),
                    last.RestoreStatus ?? first.RestoreStatus ?? SystemChangeStatuses.Pending,
                    last.Message);
            })
            .OrderByDescending(change => change.Timestamp)
            .ToList();
    }

    private static bool IsSystemChangeRecord(PersistentActivityRecord record) =>
        record.SessionId.HasValue && record.ActionId.HasValue && !string.IsNullOrWhiteSpace(record.RestoreStatus);

    private static string ChangeKey(PersistentActivityRecord record) =>
        $"{record.SessionId:N}:{record.ActionId:N}";

    private static bool RecordBelongsToFile(PersistentActivityRecord record, string path) =>
        Path.GetFileName(path).Equals($"activity-{record.Timestamp.ToLocalTime():yyyy-MM-dd}.jsonl",
            StringComparison.OrdinalIgnoreCase);

    private static PersistentActivityRecord Clone(PersistentActivityRecord record) => new()
    {
        Timestamp = record.Timestamp,
        SessionId = record.SessionId,
        ProfileId = record.ProfileId,
        ProfileName = record.ProfileName,
        ActionId = record.ActionId,
        ActionType = record.ActionType,
        FriendlyName = record.FriendlyName,
        EventType = record.EventType,
        Level = record.Level,
        StateBefore = record.StateBefore?.DeepClone().AsObject(),
        RequestedState = record.RequestedState?.DeepClone().AsObject(),
        StateAfter = record.StateAfter?.DeepClone().AsObject(),
        Result = record.Result,
        RestoreStatus = record.RestoreStatus,
        Message = record.Message
    };

    private static bool MatchesRuntime(string actual, string? expected) => string.IsNullOrWhiteSpace(expected) ||
        string.Equals(RuntimeId(actual), expected, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesStartup(string actual, string? expected) => string.IsNullOrWhiteSpace(expected) ||
        string.Equals(StartupId(actual), expected, StringComparison.OrdinalIgnoreCase);

    private static string RuntimeId(string value) => value == "Running" ? ServiceDesiredStateIds.Running :
        value == "Stopped" ? ServiceDesiredStateIds.Stopped : value;

    private static string StartupId(string value) => value switch
    {
        "Automatic" => ServiceStartupTypeIds.Automatic,
        "Automatic (Delayed Start)" => ServiceStartupTypeIds.AutomaticDelayed,
        "Manual" => ServiceStartupTypeIds.Manual,
        "Disabled" => ServiceStartupTypeIds.Disabled,
        _ => value
    };

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
