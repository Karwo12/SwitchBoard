using System.Windows.Media;

namespace SwitchBoard.ViewModels;

public sealed class CustomThemeColorItemViewModel(
    string key, string displayName, Func<string> read, Action<string> write, Action preview) : ObservableObject
{
    private string _color = read();
    public string Key { get; } = key;
    public string DisplayName { get; } = displayName;
    public bool IsValid => TryColor(_color, out _);
    public Brush PreviewBrush => TryColor(_color, out var color) ? new SolidColorBrush(color) : Brushes.Transparent;

    public string Color
    {
        get => _color;
        set
        {
            if (!SetProperty(ref _color, value)) return;
            OnPropertyChanged(nameof(IsValid));
            OnPropertyChanged(nameof(PreviewBrush));
            if (!TryColor(value, out _)) return;
            write(value);
            preview();
        }
    }

    public static bool TryColor(string? value, out Color color)
    {
        try { color = (Color)ColorConverter.ConvertFromString(value ?? string.Empty); return true; }
        catch (FormatException) { color = Colors.Transparent; return false; }
    }
}
