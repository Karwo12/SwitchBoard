namespace SwitchBoard.RuntimeTests.TestInfrastructure;

sealed class TestDisplayConfirmationService(bool result, TimeSpan? delay = null) : IDisplayConfirmationService
{
    public async Task<bool> ConfirmAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (delay is { } wait) await Task.Delay(wait, cancellationToken);
        return result;
    }
}

sealed class TestLocalizationService : ILocalizationService
{
    public IReadOnlyList<LanguageDefinition> AvailableLanguages =>
        [new("en", "English", new Uri("Localization/Strings.en.xaml", UriKind.Relative))];
    public string CurrentLanguageId => "en";
    public string DetectSystemLanguage() => "en";
    public string ApplyLanguage(string? languageId) => languageId ?? "en";
    public string GetString(string resourceKey) => resourceKey;
    public string Format(string resourceKey, params object?[] arguments) => resourceKey == "CustomTheme.CopyName"
        ? $"{arguments[0]} \u2014 copy"
        : $"{resourceKey}: {string.Join(", ", arguments)}";
}

sealed class TestCatalogService : IProfileCatalogService
{
    public SwitchBoardCatalog Saved { get; private set; } = SwitchBoardCatalog.Empty();
    public int SaveCount { get; private set; }
    public Task<SwitchBoardCatalog> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Saved);
    public Task SaveAsync(SwitchBoardCatalog catalog, CancellationToken cancellationToken = default)
    {
        SaveCount++;
        Saved = JsonSerializer.Deserialize<SwitchBoardCatalog>(JsonSerializer.Serialize(catalog))!;
        return Task.CompletedTask;
    }
}

sealed class TestSettingsRepository : ISettingsRepository
{
    public UserSettings Saved { get; private set; } = new();
    public int SaveCount { get; private set; }
    public Task<UserSettings> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(Saved))!);
    public Task SaveAsync(UserSettings settings, CancellationToken cancellationToken = default)
    {
        SaveCount++;
        Saved = JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(settings))!;
        return Task.CompletedTask;
    }
}

sealed class TestThemeManager : IThemeManager
{
    private readonly IReadOnlyList<ThemeDefinition> _availableThemes;

    public TestThemeManager(IReadOnlyList<ThemeDefinition>? availableThemes = null) =>
        _availableThemes = availableThemes ??
            [new(ThemeIds.Graphite, "Graphite", new Uri("Themes/GraphiteTheme.xaml", UriKind.Relative))];

    public IReadOnlyList<ThemeDefinition> AvailableThemes => _availableThemes;
    public string CurrentThemeId { get; private set; } = ThemeIds.Graphite;
    public string ApplyTheme(string? themeId, CustomThemeSettings? customTheme = null)
    {
        CurrentThemeId = customTheme is not null && !string.IsNullOrWhiteSpace(themeId) ? themeId : ThemeIds.Graphite;
        return CurrentThemeId;
    }
    public string ApplyTemporary(string draftThemeId, CustomThemeSettings draft)
    {
        CurrentThemeId = draftThemeId;
        LastTemporarySettings = draft.Clone();
        TemporaryApplyCount++;
        return CurrentThemeId;
    }
    public CustomThemeSettings? LastTemporarySettings { get; private set; }
    public int TemporaryApplyCount { get; private set; }
    public CustomThemeSettings CreateEditableCopy(string builtInThemeId) => CustomThemeSettings.CreateDefault();
}

sealed class TestCustomThemeEditorService : ICustomThemeEditorService
{
    public Queue<CustomThemeEditResult?> Results { get; } = [];
    public Queue<Action<CustomThemeEditRequest>> EditActions { get; } = [];
    public Queue<string?> RenameResults { get; } = [];
    public List<CustomThemeEditRequest> Requests { get; } = [];
    public bool EchoWhenEmpty { get; set; }
    public Task<CustomThemeEditResult?> EditAsync(CustomThemeEditRequest request)
    {
        Requests.Add(request with { Colors = request.Colors.Clone() });
        if (EditActions.Count > 0) EditActions.Dequeue()(request);
        var result = Results.Count > 0 ? Results.Dequeue()
            : EchoWhenEmpty ? new(request.Name, request.Colors.Clone()) : null;
        return Task.FromResult(result);
    }
    public string? Rename(string currentName, IReadOnlyCollection<string> unavailableNames) =>
        RenameResults.Count > 0 ? RenameResults.Dequeue() : null;
}

sealed class TestCompletionBehavior : IProfileCompletionBehavior
{
    public void HandleSuccessfulCompletion(ProfileDefinition profile) { }
}

sealed class TestDialogService : IUserDialogService
{
    public SwitchBoard.Services.Discovery.ProcessCandidate? ProcessSelection { get; set; }
    public bool ConfirmResult { get; set; } = true;
    public List<(string Title, string Message)> Confirmations { get; } = [];

    public bool Confirm(string title, string message)
    {
        Confirmations.Add((title, message));
        return ConfirmResult;
    }
    public string? SelectFile(string title, string filter, string? initialPath = null) => null;
      public string? SelectFolder(string title, string? initialPath = null) => null;
      public string? SelectArguments(string title, string? initialArguments = null) => null;
    public SwitchBoard.Services.Discovery.ProcessCandidate? SelectProcess(string title) => ProcessSelection;
    public SwitchBoard.Services.Discovery.ServiceCandidate? SelectService(string title) => null;
    public SwitchBoard.Services.Discovery.PowerPlanCandidate? SelectPowerPlan(string title) => null;
    public SwitchBoard.Services.Discovery.DisplayCandidate? SelectDisplay(string title) => null;
    public SwitchBoard.Services.Discovery.ProgramCandidate? FindProgram(string title) => null;
    public SwitchBoard.Services.Discovery.AudioDeviceCandidate? SelectAudioDevice(string title, bool input) => null;
    public SwitchBoard.Services.Discovery.DeviceCandidate? SelectDevice(string title) => null;
}

sealed class TestServiceManager(string initialState, bool changeSucceeds,
    string initialStartupType = ServiceStartupTypeIds.Automatic) : IWindowsServiceManager
{
    private string _state = initialState;
    private string _startupType = initialStartupType;
    public WindowsServiceSnapshot Snapshot => new(ToDisplay(_state), StartupDisplay(_startupType));
    public Task<IReadOnlyList<SwitchBoard.Services.Discovery.ServiceCandidate>> GetServicesAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SwitchBoard.Services.Discovery.ServiceCandidate>>([]);
    public Task<string> GetStateAsync(string serviceName, CancellationToken cancellationToken = default) =>
        Task.FromResult(_state);
    public Task<WindowsServiceSnapshot> GetSnapshotAsync(string serviceName,
        CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);
    public Task<WindowsServiceOperationResult> SetStateAsync(string serviceName, string desiredState, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var before = ToDisplay(_state);
        if (string.Equals(_state, desiredState, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new WindowsServiceOperationResult(true, true, StateBefore: before,
                CurrentState: before, ExpectedState: ToDisplay(desiredState)));
        if (!changeSucceeds)
            return Task.FromResult(new WindowsServiceOperationResult(false, false, "Access denied.", before,
                before, ToDisplay(desiredState), 5));
        _state = desiredState;
        return Task.FromResult(new WindowsServiceOperationResult(true, false, StateBefore: before,
            CurrentState: ToDisplay(_state), ExpectedState: ToDisplay(desiredState)));
    }
    public Task<WindowsServiceConfigurationResult> SetConfigurationAsync(string serviceName, string desiredState,
        string desiredStartupType, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var before = new WindowsServiceSnapshot(ToDisplay(_state), StartupDisplay(_startupType));
        if (!changeSucceeds)
            return Task.FromResult(new WindowsServiceConfigurationResult(false, false, before, before,
                desiredState, desiredStartupType, "Access denied.", 5));
        if (desiredState != ServiceDesiredStateIds.Unchanged) _state = desiredState;
        if (desiredStartupType != ServiceStartupTypeIds.Unchanged) _startupType = desiredStartupType;
        var current = new WindowsServiceSnapshot(ToDisplay(_state), StartupDisplay(_startupType));
        return Task.FromResult(new WindowsServiceConfigurationResult(true, before == current, before, current,
            desiredState, desiredStartupType));
    }
    private static string ToDisplay(string state) =>
        string.Equals(state, ServiceDesiredStateIds.Running, StringComparison.OrdinalIgnoreCase) ? "Running" : "Stopped";
    private static string StartupDisplay(string value) => value switch
    {
        ServiceStartupTypeIds.Automatic => "Automatic",
        ServiceStartupTypeIds.AutomaticDelayed => "Automatic (Delayed Start)",
        ServiceStartupTypeIds.Manual => "Manual",
        ServiceStartupTypeIds.Disabled => "Disabled",
        _ => value
    };
}

sealed class TestDisplayManager(DisplayModeState initialState) : IDisplayManager
{
    public DisplayModeState State { get; private set; } = initialState;
    public Task<IReadOnlyList<SwitchBoard.Services.Discovery.DisplayCandidate>> GetDisplaysAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SwitchBoard.Services.Discovery.DisplayCandidate>>([]);
    public Task<DisplayModeState> GetCurrentStateAsync(string deviceId, string deviceName, CancellationToken cancellationToken = default) =>
        Task.FromResult(State);
    public Task ApplyTemporaryAsync(DisplayModeState state, CancellationToken cancellationToken = default) { State = state; return Task.CompletedTask; }
    public Task PersistAsync(DisplayModeState state, CancellationToken cancellationToken = default) { State = state; return Task.CompletedTask; }
    public Task RestoreAsync(DisplayModeState state, CancellationToken cancellationToken = default) { State = state; return Task.CompletedTask; }
}

public sealed class TestReversibleHandler(List<string> restoreOrder, IExecutionSessionRepository repository) : IReversibleActionHandler
{
    public const string TypeId = "test.reversible";
    public string ActionType => TypeId;
    public bool CaptureWasPersistedBeforeExecute { get; private set; } = true;
    public Dictionary<string, int> RestoreAttempts { get; } = [];
    private readonly HashSet<string> _failedOnce = [];

    public Task<JsonObject?> CaptureStateAsync(ActionDefinition action, ActionExecutionContext context,
        CancellationToken cancellationToken) => Task.FromResult<JsonObject?>(new JsonObject
        {
            ["key"] = action.Parameters["key"]?.GetValue<string>(),
            ["failOnce"] = action.Parameters["failOnce"]?.GetValue<bool>() ?? false,
            ["restoreDelayMs"] = action.Parameters["restoreDelayMs"]?.GetValue<int>() ?? 0
        });

    public async Task<ActionExecutionResult> ExecuteAsync(ActionDefinition action, ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var session = await repository.LoadAsync(context.SessionId, cancellationToken);
        var item = session?.Actions.SingleOrDefault(candidate => candidate.ActionId == action.Id);
        CaptureWasPersistedBeforeExecute &= item?.PreviousState is not null && item.ExecutionStatus == PersistentActionExecutionStatus.Running;
        return ActionExecutionResult.Success();
    }

    public async Task<ActionExecutionResult> RestoreAsync(ActionDefinition action, JsonObject restoreState, ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var key = restoreState["key"]?.GetValue<string>() ?? string.Empty;
        restoreOrder.Add(key);
        RestoreAttempts[key] = RestoreAttempts.GetValueOrDefault(key) + 1;
        if ((restoreState["failOnce"]?.GetValue<bool>() ?? false) && _failedOnce.Add(key))
            throw new InvalidOperationException("Simulated first restore failure.");
        var delay = restoreState["restoreDelayMs"]?.GetValue<int>() ?? 0;
        if (delay > 0) await Task.Delay(delay, cancellationToken);
        return ActionExecutionResult.Success("Verified test restore.");
    }
}




