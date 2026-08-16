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
            settings.CustomTheme ??= CustomThemeSettings.CreateDefault();
            var themeManager = new ThemeManager(paths);
            settings.ThemeId = themeManager.ApplyTheme(settings.ThemeId, settings.CustomTheme);
            settings.SchemaVersion = SettingsSchema.CurrentVersion;
            await _settingsRepository.SaveAsync(settings);
            var catalogService = new ProfileCatalogService(_repository);
            var catalog = await catalogService.LoadAsync();
            var windowsServiceManager = new WindowsServiceManager();
            var powerPlanManager = new WindowsPowerPlanManager();
            var displayManager = new WindowsDisplayManager();
            var displayConfirmationService = new WpfDisplayConfirmationService(_localizationService);
            var actionRegistry = new ActionRegistry
            ([
                new ProgramRunActionHandler(),
                new ProcessSetStateActionHandler(),
                new ServiceSetStateActionHandler(windowsServiceManager),
                new PowerSetPlanActionHandler(powerPlanManager),
                new DisplayConfigureActionHandler(displayManager, displayConfirmationService),
                new ScriptRunActionHandler(),
                new DelayActionHandler()
            ]);
            var profileRunner = new ProfileRunner(actionRegistry, _sessionRepository, _logger);
            var restoreRunner = new ProfileRestoreRunner(actionRegistry, _sessionRepository, _logger);
            var completionBehavior = new ProfileCompletionBehavior(new WpfApplicationLifetime());
            var processDiscoveryService = new WindowsProcessDiscoveryService();
            var programDiscoveryService = new WindowsProgramDiscoveryService();
            var viewModel = new MainWindowViewModel(
                catalogService,
                new WpfUserDialogService(
                    processDiscoveryService,
                    programDiscoveryService,
                    windowsServiceManager,
                    powerPlanManager,
                    displayManager,
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
                new WpfCustomThemeEditorService(paths, _localizationService));

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
}

