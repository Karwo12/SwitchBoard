using SwitchBoard.RuntimeTests.TestInfrastructure;
using SwitchBoard.Services.Monitoring;

namespace SwitchBoard.RuntimeTests.ViewModels;

[Collection("Windows runtime")]
public sealed class StatusRefreshRegressionTests
{
    [Fact]
    [Trait("Category", "Regression")]
    public async Task ChangingProfileDuringRefresh_RefreshesTheNewlySelectedProfile()
    {
        using var fixture = new StatusRefreshFixture();

        await fixture.Service.FirstRefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Main.SelectedProfile = fixture.Main.AllProfiles.Single(profile => profile.Id == fixture.SecondProfile.Id);
        fixture.Service.ReleaseFirstRefresh.TrySetResult(true);

        await fixture.Service.SecondRefreshCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(new[] { "first", "second" }, fixture.Service.RequestedServices);
        Assert.Contains("Status=Running", fixture.Main.SelectedProfile!.Actions.Single().CurrentStatusTooltip,
            StringComparison.Ordinal);
    }

    private sealed class StatusRefreshFixture : IDisposable
    {
        public StatusRefreshFixture()
        {
            Context = new RuntimeTestContext();
            Service = new BlockingServiceManager();
            FirstProfile = CreateProfile("First", "first");
            SecondProfile = CreateProfile("Second", "second");
            var monitoring = new StatusMonitoringService(Service, new NoopPowerPlanManager(),
                new TestDisplayManager(new("", "", "", 1, 1, 1, 32, 0, 0, 0, 0)), new NoopAudioManager(),
                new NoopDeviceManager(), new NoopProcessDiscoveryService(), new TestLocalizationService());
            Main = new MainWindowViewModel(new TestCatalogService(), new TestDialogService(), new SwitchBoardCatalog
                {
                    Profiles = [FirstProfile, SecondProfile]
                }, new TestThemeManager(), new TestLocalizationService(), new TestSettingsRepository(),
                new UserSettings { ThemeId = ThemeIds.Graphite, LanguageId = "en" }, Context.Runner,
                new ProfileRestoreRunner(Context.Registry, Context.SessionRepository), Context.SessionRepository,
                new TestCompletionBehavior(), new TestDisplayManager(new("", "", "", 1, 1, 1, 32, 0, 0, 0, 0)),
                new TestCustomThemeEditorService(), statusMonitoring: monitoring);
        }

        public RuntimeTestContext Context { get; }
        public BlockingServiceManager Service { get; }
        public ProfileDefinition FirstProfile { get; }
        public ProfileDefinition SecondProfile { get; }
        public MainWindowViewModel Main { get; }

        public void Dispose()
        {
            Service.ReleaseFirstRefresh.TrySetResult(true);
            Main.Dispose();
            Context.Dispose();
        }

        private static ProfileDefinition CreateProfile(string profileName, string serviceName) => new()
        {
            Name = profileName,
            Actions =
            [
                new ActionDefinition
                {
                    Type = ActionTypeIds.ServiceSetState,
                    Parameters = new JsonObject { [ActionParameterNames.ServiceName] = serviceName }
                }
            ]
        };
    }

    private sealed class BlockingServiceManager : IWindowsServiceManager
    {
        private readonly List<string> _requestedServices = [];

        public TaskCompletionSource<bool> FirstRefreshStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseFirstRefresh { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> SecondRefreshCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<string> RequestedServices
        {
            get { lock (_requestedServices) return _requestedServices.ToList(); }
        }

        public Task<IReadOnlyList<ServiceCandidate>> GetServicesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ServiceCandidate>>([]);
        public Task<string> GetStateAsync(string serviceName, CancellationToken cancellationToken = default) =>
            Task.FromResult("Running");
        public async Task<WindowsServiceSnapshot> GetSnapshotAsync(string serviceName,
            CancellationToken cancellationToken = default)
        {
            lock (_requestedServices) _requestedServices.Add(serviceName);
            if (serviceName == "first")
            {
                FirstRefreshStarted.TrySetResult(true);
                await ReleaseFirstRefresh.Task.WaitAsync(cancellationToken);
            }
            if (serviceName == "second") SecondRefreshCompleted.TrySetResult(true);
            return new WindowsServiceSnapshot("Running", "Automatic");
        }
        public Task<WindowsServiceOperationResult> SetStateAsync(string serviceName, string desiredState,
            TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WindowsServiceOperationResult(true, false));
        public Task<WindowsServiceConfigurationResult> SetConfigurationAsync(string serviceName, string desiredState,
            string desiredStartupType, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WindowsServiceConfigurationResult(true, false, null, null, desiredState,
                desiredStartupType));
    }

    private sealed class NoopPowerPlanManager : IPowerPlanManager
    {
        public Task<IReadOnlyList<PowerPlanCandidate>> GetPlansAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PowerPlanCandidate>>([]);
        public Task<Guid> GetActivePlanAsync(CancellationToken cancellationToken = default) => Task.FromResult(Guid.Empty);
        public Task SetActivePlanAsync(Guid planId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoopAudioManager : IAudioManager
    {
        public Task<IReadOnlyList<AudioDeviceCandidate>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AudioDeviceCandidate>>([]);
        public Task<string?> GetDefaultDeviceIdAsync(bool input, bool communications,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task SetDefaultDeviceAsync(string deviceId, bool multimedia, bool communications,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<(float Volume, bool Muted)> GetMasterVolumeAsync(string? deviceId = null,
            CancellationToken cancellationToken = default) => Task.FromResult((0f, false));
        public Task SetMasterVolumeAsync(float? volume, bool? muted, string? deviceId = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoopDeviceManager : IDeviceManager
    {
        public Task<IReadOnlyList<DeviceCandidate>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DeviceCandidate>>([]);
        public Task<DeviceCandidate?> GetDeviceAsync(string instanceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<DeviceCandidate?>(null);
        public Task SetEnabledAsync(string instanceId, bool enabled, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class NoopProcessDiscoveryService : IProcessDiscoveryService
    {
        public Task<IReadOnlyList<ProcessCandidate>> GetProcessesAsync(CancellationToken cancellationToken = default,
            bool includeIcons = true) => Task.FromResult<IReadOnlyList<ProcessCandidate>>([]);
    }
}
