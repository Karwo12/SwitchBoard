using System.Windows.Input;

namespace SwitchBoard.ViewModels;

public sealed class AsyncRelayCommand<T>(Func<T?, Task> execute, Predicate<T?>? canExecute = null) : ICommand
{
    private bool _isRunning;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        !_isRunning && (canExecute?.Invoke(ConvertParameter(parameter)) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isRunning = true;
        NotifyCanExecuteChanged();
        try
        {
            await execute(ConvertParameter(parameter));
        }
        finally
        {
            _isRunning = false;
            NotifyCanExecuteChanged();
        }
    }

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private static T? ConvertParameter(object? parameter) => parameter is T value ? value : default;
}
