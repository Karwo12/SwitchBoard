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
    private readonly ThemeColorPickerControl _colorPicker;
    private CustomThemeColorItemViewModel? _editingColor;
    private string? _editingColorInitialText;
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

        _colorPicker = new ThemeColorPickerControl(Colors.Transparent, localization,
            color => { if (_editingColor is not null) _editingColor.Color = color.ToString(); });
        _colorPicker.Confirmed += ColorPicker_OnConfirmed;
        _colorPicker.Canceled += ColorPicker_OnCanceled;
        ColorPickerPopup.Child = _colorPicker;
    }

    public CustomThemeEditorViewModel ViewModel { get; }
    public CustomThemeEditResult? Result { get; private set; }

    private void ColorButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not CustomThemeColorItemViewModel item) return;
        if (ReferenceEquals(_editingColor, item) && ColorPickerPopup.IsOpen) return;

        // Keep one popup instance alive when moving directly to another color.
        // The previous draft is discarded before the new field is attached to it.
        CloseColorPicker(restore: true, closePopup: false);
        var initial = CustomThemeColorItemViewModel.TryColor(item.Color, out var color) ? color : Colors.White;
        _editingColor = item;
        _editingColorInitialText = item.Color;
        _colorPicker.BeginEdit(initial);
        ColorPickerPopup.PlacementTarget = button;
        ColorPickerPopup.IsOpen = true;
        e.Handled = true;
    }

    private void ColorPicker_OnConfirmed(object? sender, EventArgs e) => CloseColorPicker(restore: false);
    private void ColorPicker_OnCanceled(object? sender, EventArgs e) => CloseColorPicker(restore: true);

    private void ColorPickerPopup_OnClosed(object? sender, EventArgs e) => CloseColorPicker(restore: true);

    private void CloseColorPicker(bool restore, bool closePopup = true)
    {
        if (restore && _editingColor is not null && _editingColorInitialText is not null)
            _editingColor.Color = _editingColorInitialText;

        _editingColor = null;
        _editingColorInitialText = null;
        if (closePopup && ColorPickerPopup.IsOpen) ColorPickerPopup.IsOpen = false;
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
            if (extension is not (".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif" or ".mp4"))
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

    private void Reset_OnClick(object sender, RoutedEventArgs e)
    {
        CloseColorPicker(restore: true);
        ViewModel.Reset();
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e)
    {
        CloseColorPicker(restore: true);
        Close();
    }

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        // Clicking Save outside the popup must not accept an unconfirmed picker edit.
        CloseColorPicker(restore: true);
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
        CloseColorPicker(restore: true);
        ColorPickerPopup.Child = null;
        // A canceled MP4 preview owns a native MediaPlayer. Clearing the temporary
        // theme before removing a newly selected asset makes the background host
        // close that player and release the file handle deterministically.
        if (!_saved) ViewModel.ClearTemporaryBackground();
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
