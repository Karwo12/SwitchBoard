namespace SwitchBoard.ViewModels;

public sealed class LogicalCpuOptionViewModel(int index, bool isSelected) : ObservableObject
{
    private bool _isSelected = isSelected;
    public int Index { get; } = index;
    public string DisplayName => $"CPU {Index}";
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}
