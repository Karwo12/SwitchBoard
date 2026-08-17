using System.Configuration;
using System.Data;
using System.Windows;
using SwitchBoard.Data;
using SwitchBoard.Localization;
using SwitchBoard.Services;
using SwitchBoard.Services.ApplicationLifecycle;
using SwitchBoard.Services.Discovery;
using SwitchBoard.Services.Execution;
using SwitchBoard.Services.Execution.Handlers;
using SwitchBoard.Services.Persistence;
using SwitchBoard.Services.Profiles;
using SwitchBoard.Themes;
using SwitchBoard.ViewModels;
using SwitchBoard.Views;
using SwitchBoard.Services.Windows;
using SwitchBoard.Services.Logging;
using SwitchBoard.Services.Activity;
using SwitchBoard.Services.Monitoring;
using SwitchBoard.Models.Actions;

namespace SwitchBoard;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private JsonCatalogRepository? _repository;
    private JsonSettingsRepository? _settingsRepository;
    private JsonExecutionSessionRepository? _sessionRepository;
    private LocalizationService? _localizationService;
    private IAppLogger? _logger;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (DisplayRollbackGuard.TryRun(e.Args))
        {
            Shutdown();
            return;
        }

        try
        {
            var paths = new AppDataPaths();
            _logger = new RollingFileLogger(paths);
            _logger.Info("Startup", "SwitchBoard startup began.");
            DispatcherUnhandledException += (_, args) =>
            {
                _logger?.Error("Dispatcher", args.Exception, "Unhandled UI exception.");
            };
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception exception) _logger?.Error("AppDomain", exception, "Unhandled application exception.");
            };
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                _logger?.Error("TaskScheduler", args.Exception, "Unobserved task exception.");
                args.SetObserved();
            };
            _repository = new JsonCatalogRepository(paths);
            _settingsRepository = new JsonSettingsRepository(paths);
            _sessionRepository = new JsonExecutionSessionRepository(paths);
            await _sessionRepository.MaintainAsync(TimeSpan.FromDays(30));
            var settings = await _settingsRepository.LoadAsync();
            _localizationService = new LocalizationService();
            settings.LanguageId = _localizationService.ApplyLanguage(
                settings.LanguageId ?? _localizationService.DetectSystemLanguage());
            settings.CustomThemes ??= [];
            if (settings.CustomTheme is not null)
            {
                var migrated = new CustomThemeDefinition
                {
                    Name = _localizationService.GetString("CustomTheme.MigratedName"),
                    Colors = settings.CustomTheme.Clone()
                };
                settings.CustomThemes.Add(migrated);
                if (string.Equals(settings.ThemeId, ThemeIds.Custom, StringComparison.OrdinalIgnoreCase))
                    settings.ThemeId = migrated.Id;
                settings.CustomTheme = null;
            }
            if (settings.SchemaVersion < SettingsSchema.SurfaceOpacityVersion)
                foreach (var theme in settings.CustomThemes)
                    theme.Colors.MigrateSurfaceOpacityFromLegacyAlpha();
            if (settings.SchemaVersion < SettingsSchema.ActivityOpacityVersion)
                foreach (var theme in settings.CustomThemes)
                    theme.Colors.MigrateActivityOpacity();
            var themeManager = new ThemeManager(paths);
            NormalizeCustomThemes(settings.CustomThemes, _localizationService.GetString("CustomTheme.DefaultName"),
                themeManager.AvailableThemes.Select(theme => _localizationService.GetString(theme.DisplayNameResourceKey)));
            var activeCustomTheme = settings.CustomThemes.FirstOrDefault(theme =>
                string.Equals(theme.Id, settings.ThemeId, StringComparison.OrdinalIgnoreCase));
            settings.ThemeId = themeManager.ApplyTheme(settings.ThemeId, activeCustomTheme?.Colors);
            settings.SchemaVersion = SettingsSchema.CurrentVersion;
            await _settingsRepository.SaveAsync(settings);
            var catalogService = new ProfileCatalogService(_repository);
            var catalog = await catalogService.LoadAsync();
            var windowsServiceManager = new WindowsServiceManager();
            var powerPlanManager = new WindowsPowerPlanManager();
            var displayManager = new WindowsDisplayManager();
            var audioManager = new WindowsAudioManager();
            var deviceManager = new WindowsDeviceManager();
            var activityService = new ActivityService(paths, _localizationService, _logger);
            await activityService.ReconcileServiceChangesAsync(windowsServiceManager);
            var displayConfirmationService = new WpfDisplayConfirmationService(_localizationService);
            var actionRegistry = new ActionRegistry
            ([
                new ProgramRunActionHandler(_localizationService),
                new ProcessSetStateActionHandler(),
                new ServiceSetStateActionHandler(windowsServiceManager, _localizationService),
                new PowerSetPlanActionHandler(powerPlanManager),
                new DisplayConfigureActionHandler(displayManager, displayConfirmationService),
                new ScriptRunActionHandler(),
                new DelayActionHandler(),
                new ProcessConfigureActionHandler(),
                new WaitProcessActionHandler(ActionTypeIds.WaitProcessStart),
                new WaitProcessActionHandler(ActionTypeIds.WaitProcessExit),
                new WaitWindowActionHandler(),
                new AudioConfigureActionHandler(audioManager),
                new DeviceSetStateActionHandler(deviceManager),
                new ProfileRunActionHandler(),
                new ConditionIfActionHandler(windowsServiceManager),
                new NotificationShowActionHandler(activityService)
            ]);
            MainWindowViewModel? viewModel = null;
            var profileRunner = new ProfileRunner(actionRegistry, _sessionRepository, _logger,
                id => viewModel?.ResolveProfileDefinition(id) ?? catalog.Profiles.FirstOrDefault(item => item.Id == id),
                activityService, _localizationService);
            var restoreRunner = new ProfileRestoreRunner(actionRegistry, _sessionRepository, _logger,
                activityService, _localizationService);
            var completionBehavior = new ProfileCompletionBehavior(new WpfApplicationLifetime());
            var processDiscoveryService = new WindowsProcessDiscoveryService();
            var programDiscoveryService = new WindowsProgramDiscoveryService();
            var statusMonitoring = new StatusMonitoringService(windowsServiceManager, powerPlanManager, displayManager,
                audioManager, deviceManager, processDiscoveryService, _localizationService);
            viewModel = new MainWindowViewModel(
                catalogService,
                new WpfUserDialogService(
                    processDiscoveryService,
                    programDiscoveryService,
                    windowsServiceManager,
                    powerPlanManager,
                    displayManager,
                    audioManager,
                    deviceManager,
                    _localizationService),
                catalog,
                themeManager,
                _localizationService,
                _settingsRepository,
                settings,
                profileRunner,
                restoreRunner,
                _sessionRepository,
                completionBehavior,
                displayManager,
                new WpfCustomThemeEditorService(paths, _localizationService),
                activityService, statusMonitoring);

            var window = new MainWindow(viewModel);
            MainWindow = window;
            window.Show();
            _logger.Info("Startup", "Main window opened successfully.");
        }
        catch (Exception exception)
        {
            _logger?.Error("Startup", exception, "SwitchBoard startup failed.");
            MessageBox.Show(
                _localizationService?.Format("Dialog.StartupErrorMessage", exception.Message)
                    ?? $"SwitchBoard could not start.\n\n{exception.Message}",
                _localizationService?.GetString("Dialog.StartupErrorTitle") ?? "Startup error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _repository?.Dispose();
        _settingsRepository?.Dispose();
        _sessionRepository?.Dispose();
        base.OnExit(e);
    }

    private static void NormalizeCustomThemes(List<CustomThemeDefinition> themes, string fallbackName,
        IEnumerable<string> builtInNames)
    {
        var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(builtInNames, StringComparer.CurrentCultureIgnoreCase);
        foreach (var theme in themes)
        {
            if (string.IsNullOrWhiteSpace(theme.Id) || !identifiers.Add(theme.Id))
            {
                theme.Id = CustomThemeDefinition.CreateId();
                identifiers.Add(theme.Id);
            }
            theme.Colors ??= CustomThemeSettings.CreateDefault();
            theme.Colors.NormalizeLegacy();
            theme.IsBuiltIn = false;
            var baseName = string.IsNullOrWhiteSpace(theme.Name) ? fallbackName : theme.Name.Trim();
            var candidate = baseName;
            var suffix = 2;
            while (!names.Add(candidate)) candidate = $"{baseName} ({suffix++})";
            theme.Name = candidate;
            if (theme.CreatedAt == default) theme.CreatedAt = DateTimeOffset.UtcNow;
            if (theme.UpdatedAt == default) theme.UpdatedAt = theme.CreatedAt;
        }
    }
}

