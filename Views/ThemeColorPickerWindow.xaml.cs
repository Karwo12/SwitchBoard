using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SwitchBoard.Localization;
using SwitchBoard.ViewModels;

namespace SwitchBoard.Views;

public partial class ThemeColorPickerWindow : Window
{
    private readonly ILocalizationService _localization;
    private readonly Action<Color>? _previewChanged;
    private readonly Color _initialColor;
    private bool _updating;

    public ThemeColorPickerWindow(Color initial, ILocalizationService localization, Action<Color>? previewChanged = null)
    {
        InitializeComponent();
        _localization = localization;
        _previewChanged = previewChanged;
        _initialColor = initial;
        SetColor(initial);
    }

    public Color SelectedColor { get; private set; }

    private void Channel_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || Preview is null) return;
        SelectedColor = Color.FromArgb((byte)Alpha.Value, (byte)Red.Value, (byte)Green.Value, (byte)Blue.Value);
        RefreshDisplay(updateHex: true);
        _previewChanged?.Invoke(SelectedColor);
    }

    private void SetColor(Color color)
    {
        _updating = true;
        SelectedColor = color;
        Alpha.Value = color.A; Red.Value = color.R; Green.Value = color.G; Blue.Value = color.B;
        _updating = false;
        RefreshDisplay(updateHex: true);
    }

    private void RefreshDisplay(bool updateHex)
    {
        Preview.Background = new SolidColorBrush(SelectedColor);
        AlphaValue.Text = SelectedColor.A.ToString(); RedValue.Text = SelectedColor.R.ToString();
        GreenValue.Text = SelectedColor.G.ToString(); BlueValue.Text = SelectedColor.B.ToString();
        if (updateHex)
        {
            _updating = true;
            HexValue.Text = SelectedColor.ToString();
            _updating = false;
        }
        ErrorText.Text = string.Empty;
    }

    private bool ApplyHex()
    {
        if (!CustomThemeColorItemViewModel.TryColor(HexValue.Text, out var color))
        {
            ErrorText.Text = _localization.GetString("CustomTheme.InvalidColor");
            return false;
        }
        SetColor(color);
        _previewChanged?.Invoke(SelectedColor);
        return true;
    }

    private void HexValue_OnLostFocus(object sender, RoutedEventArgs e) => ApplyHex();
    private void HexValue_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_updating || !CustomThemeColorItemViewModel.TryColor(HexValue.Text, out var color)) return;
        SetColor(color);
        _previewChanged?.Invoke(SelectedColor);
    }
    private void HexValue_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyHex();
        e.Handled = true;
    }
    private void Save_OnClick(object sender, RoutedEventArgs e) { if (ApplyHex()) DialogResult = true; }
    private void Cancel_OnClick(object sender, RoutedEventArgs e)
    {
        _previewChanged?.Invoke(_initialColor);
        Close();
    }
}
