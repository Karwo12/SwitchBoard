using System.ComponentModel;
using System.Windows.Media;
using SwitchBoard.Services.Discovery;

namespace SwitchBoard.ViewModels;

/// <summary>
/// Adapts the shared action-icon pipeline for durable Activity rows. The row follows a live
/// action icon when it is still available, and otherwise keeps the same packaged action fallback.
/// </summary>
public sealed class ActivityIconViewModel : ObservableObject, IDisposable
{
    private readonly ActionItemViewModel? _sourceAction;
    private readonly ImageSource? _fallbackIcon;

    public ActivityIconViewModel(ActionItemViewModel? sourceAction, string? actionType)
    {
        _sourceAction = sourceAction;
        _fallbackIcon = FileIconCache.Shared.GetActionIcon(
            ActionItemViewModel.GetFallbackIconAsset(actionType ?? string.Empty));
        if (_sourceAction is not null)
            _sourceAction.PropertyChanged += SourceActionOnPropertyChanged;
    }

    public ImageSource? Icon => _sourceAction?.ApplicationIcon ?? _sourceAction?.ActionFallbackIcon ?? _fallbackIcon;

    public bool HasIcon => Icon is not null;

    public void Dispose()
    {
        if (_sourceAction is not null)
            _sourceAction.PropertyChanged -= SourceActionOnPropertyChanged;
    }

    private void SourceActionOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(ActionItemViewModel.ApplicationIcon) or
            nameof(ActionItemViewModel.ActionFallbackIcon))) return;

        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(HasIcon));
    }
}
