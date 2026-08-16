using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using SwitchBoard.Data;
using SwitchBoard.Localization;
using SwitchBoard.Themes;
using SwitchBoard.ViewModels;

namespace SwitchBoard.Views;

public partial class CustomThemeWindow : Window
{
    private readonly AppDataPaths _paths;
    private readonly ILocalizationService _localization;
    private readonly Action<CustomThemeSettings> _preview;
    private readonly CustomThemeSettings _initial;
    private readonly List<string> _createdAssets = [];
    private bool _saved;

    public CustomThemeWindow(CustomThemeSettings current, AppDataPaths paths, ILocalizationService localization,
        Action<CustomThemeSettings> preview)
    {
        InitializeComponent();
        _paths = paths;
        _localization = localization;
        _preview = preview;
        _initial = current.Clone();
        ViewModel = new CustomThemeEditorViewModel(current.Clone(), localization, preview);
        DataContext = ViewModel;
    }

    public CustomThemeEditorViewModel ViewModel { get; }
    public CustomThemeSettings? Result { get; private set; }

    private void ColorButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: CustomThemeColorItemViewModel item }) return;
        var initial = CustomThemeColorItemViewModel.TryColor(item.Color, out var color) ? color : Colors.White;
        if (NativeColorPicker.TryChoose(new WindowInteropHelper(this).Handle, initial, out var selected))
            item.Color = selected.ToString();
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

    private void Reset_OnClick(object sender, RoutedEventArgs e) => ViewModel.Reset(_localization);
    private void Cancel_OnClick(object sender, RoutedEventArgs e) => Close();

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Colors.Any(item => !item.IsValid))
        {
            ViewModel.Warning = _localization.GetString("CustomTheme.InvalidColor");
            return;
        }
        Result = ViewModel.Settings.Clone();
        Result.PreviewBackgroundPath = null;
        _saved = true;
        DialogResult = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_saved) _preview(_initial);
        CleanupAssets();
        base.OnClosing(e);
    }

    private void CleanupAssets()
    {
        var kept = _saved && !string.IsNullOrWhiteSpace(Result?.BackgroundAssetFileName)
            ? Path.GetFullPath(Path.Combine(_paths.CustomThemeDirectory, Result.BackgroundAssetFileName))
            : null;
        foreach (var asset in _createdAssets)
        {
            if (string.Equals(Path.GetFullPath(asset), kept, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(asset); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        // Keep the previously persisted asset. It is safer to leave a small orphan than to make the
        // last good settings.json reference a missing file if the subsequent atomic settings save fails.
    }

    private static class NativeColorPicker
    {
        private static readonly uint[] CustomColors = new uint[16];

        public static bool TryChoose(IntPtr owner, Color initial, out Color selected)
        {
            var colors = Marshal.AllocHGlobal(sizeof(uint) * CustomColors.Length);
            try
            {
                Marshal.Copy(CustomColors.Select(value => unchecked((int)value)).ToArray(), 0, colors, CustomColors.Length);
                var value = new ChooseColor
                {
                    StructSize = Marshal.SizeOf<ChooseColor>(), Owner = owner,
                    ResultColor = (uint)(initial.R | initial.G << 8 | initial.B << 16),
                    CustomColors = colors, Flags = 0x00000001 | 0x00000100
                };
                if (!ChooseColorDialog(ref value)) { selected = initial; return false; }
                var raw = value.ResultColor;
                selected = Color.FromArgb(255, (byte)(raw & 0xFF), (byte)((raw >> 8) & 0xFF), (byte)((raw >> 16) & 0xFF));
                var copied = new int[CustomColors.Length];
                Marshal.Copy(colors, copied, 0, copied.Length);
                for (var index = 0; index < copied.Length; index++) CustomColors[index] = unchecked((uint)copied[index]);
                return true;
            }
            finally { Marshal.FreeHGlobal(colors); }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ChooseColor
        {
            public int StructSize; public IntPtr Owner; public IntPtr Instance; public uint ResultColor;
            public IntPtr CustomColors; public uint Flags; public IntPtr CustomData; public IntPtr Hook; public string? TemplateName;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChooseColorW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ChooseColorDialog(ref ChooseColor value);
    }
}
