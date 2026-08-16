using System.Configuration;
using System.Data;
using System.Windows;
using SwitchBoard.Data;
using SwitchBoard.Localization;
using SwitchBoard.Services;
using SwitchBoard.Services.Persistence;
using SwitchBoard.Services.Profiles;
using SwitchBoard.Themes;
using SwitchBoard.ViewModels;
using SwitchBoard.Views;

namespace SwitchBoard;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private JsonCatalogRepository? _repository;
    private JsonSettingsRepository? _settingsRepository;
    private LocalizationService? _localizationService;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            var paths = new AppDataPaths();
            _repository = new JsonCatalogRepository(paths);
            _settingsRepository = new JsonSettingsRepository(paths);
            var settings = await _settingsRepository.LoadAsync();
            _localizationService = new LocalizationService();
            settings.LanguageId = _localizationService.ApplyLanguage(
                settings.LanguageId ?? _localizationService.DetectSystemLanguage());
            var themeManager = new ThemeManager();
            settings.ThemeId = themeManager.ApplyTheme(settings.ThemeId);
            settings.SchemaVersion = SettingsSchema.CurrentVersion;
            await _settingsRepository.SaveAsync(settings);
            var catalogService = new ProfileCatalogService(_repository);
            var catalog = await catalogService.LoadAsync();
            var viewModel = new MainWindowViewModel(
                catalogService,
                new WpfUserDialogService(),
                catalog,
                themeManager,
                _localizationService,
                _settingsRepository,
                settings);

            var window = new MainWindow(viewModel);
            MainWindow = window;
            window.Show();
        }
        catch (Exception exception)
        {
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
        base.OnExit(e);
    }
}

