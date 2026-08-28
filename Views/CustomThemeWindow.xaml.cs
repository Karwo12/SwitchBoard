using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SwitchBoard.Data;
using SwitchBoard.Localization;
using SwitchBoard.Services;
using SwitchBoard.Themes;
using SwitchBoard.ViewModels;

namespace SwitchBoard.Views;

public partial class CustomThemeWindow : Window
{
    private readonly AppDataPaths _paths;
    private readonly ILocalizationService _localization;
    private readonly List<string> _createdAssets = [];
    private bool _saved;

    public CustomThemeWindow(CustomThemeEditRequest request, AppDataPaths paths, ILocalizationService localization)
    {
        InitializeComponent();
        _paths = paths;
        _localization = localization;
        var prepared = request.Colors.Clone();
        if (!string.IsNullOrWhiteSpace(prepared.BackgroundAssetFileName))
        {
            var backgroundPath = Path.Combine(paths.CustomThemeDirectory, prepared.BackgroundAssetFileName);
            if (File.Exists(backgroundPath)) prepared.PreviewBackgroundPath = backgroundPath;
        }
        ViewModel = new CustomThemeEditorViewModel(request with { Colors = prepared }, localization);
        DataContext = ViewModel;
    }

    public CustomThemeEditorViewModel ViewModel { get; }
    public CustomThemeEditResult? Result { get; private set; }

    private void ColorButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: CustomThemeColorItemViewModel item }) return;
        var initial = CustomThemeColorItemViewModel.TryColor(item.Color, out var color) ? color : Colors.White;
        var initialText = item.Color;
        var picker = new ThemeColorPickerWindow(initial, _localization,
            selected => item.Color = selected.ToString()) { Owner = this };
        if (picker.ShowDialog() == true)
            item.Color = picker.SelectedColor.ToString();
        else
            item.Color = initialText;
    }

    private void ChooseImage_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = _localization.GetString("CustomTheme.ChooseImage"),
            Filter = _localization.GetString("CustomTheme.ImageFilter"),
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(_paths.CustomThemeDirectory);
            var extension = Path.GetExtension(dialog.FileName).ToLowerInvariant();
            if (extension is not (".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif"))
                throw new InvalidDataException(_localization.GetString("CustomTheme.InvalidImage"));
            var assetName = $"background-{Guid.NewGuid():N}{extension}";
            var target = Path.Combine(_paths.CustomThemeDirectory, assetName);
            temporary = Path.Combine(_paths.CustomThemeDirectory, $".{assetName}.tmp");
            File.Copy(dialog.FileName, temporary, true);
            File.Move(temporary, target);
            temporary = null;
            _createdAssets.Add(target);
            ViewModel.SetBackground(assetName, target);
            var size = new FileInfo(target).Length;
            ViewModel.Warning = size > 15 * 1024 * 1024
                ? _localization.GetString("CustomTheme.LargeImageWarning")
                : string.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ViewModel.Warning = exception.Message;
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    private void RemoveImage_OnClick(object sender, RoutedEventArgs e)
    {
        ViewModel.SetBackground(null, null);
        ViewModel.Warning = string.Empty;
    }

    private void Reset_OnClick(object sender, RoutedEventArgs e) => ViewModel.Reset();
    private void Cancel_OnClick(object sender, RoutedEventArgs e) => Close();

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Colors.Any(item => !item.IsValid))
        {
            ViewModel.Warning = _localization.GetString("CustomTheme.InvalidColor");
            return;
        }
        if (!ViewModel.IsNameValid)
        {
            ViewModel.Warning = ViewModel.NameError;
            return;
        }
        var colors = ViewModel.Settings.Clone();
        colors.PreviewBackgroundPath = null;
        Result = new CustomThemeEditResult(ViewModel.Name.Trim(), colors);
        _saved = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        CleanupAssets();
        base.OnClosing(e);
    }

    private void CleanupAssets()
    {
        var kept = _saved && !string.IsNullOrWhiteSpace(Result?.Colors.BackgroundAssetFileName)
            ? Path.GetFullPath(Path.Combine(_paths.CustomThemeDirectory, Result.Colors.BackgroundAssetFileName))
            : null;
        foreach (var asset in _createdAssets)
        {
            if (string.Equals(Path.GetFullPath(asset), kept, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(asset); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        // Keep the previously persisted asset. It is safer to leave a small orphan than to make the
        // last good settings.json reference a missing file if the subsequent atomic settings save fails.
    }

}
