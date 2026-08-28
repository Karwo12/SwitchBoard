using System.Windows;
using SwitchBoard.Data;
using SwitchBoard.Localization;
using SwitchBoard.Themes;
using SwitchBoard.Views;

namespace SwitchBoard.Services;

public sealed class WpfCustomThemeEditorService(AppDataPaths paths, ILocalizationService localization)
    : ICustomThemeEditorService
{
    private readonly Dictionary<string, EditorSession> _openEditors = new(StringComparer.OrdinalIgnoreCase);

    public Task<CustomThemeEditResult?> EditAsync(CustomThemeEditRequest request)
    {
        var key = request.ThemeId ?? $"{request.Mode}:{request.Name}";
        if (_openEditors.TryGetValue(key, out var existing))
        {
            Activate(existing.Window);
            return existing.Completion.Task;
        }

        var window = new CustomThemeWindow(request, paths, localization)
        {
            Owner = Application.Current.MainWindow
        };
        var session = new EditorSession(window);
        _openEditors.Add(key, session);

        EventHandler? closedHandler = null;
        closedHandler = (_, _) =>
        {
            window.Closed -= closedHandler;
            if (_openEditors.TryGetValue(key, out var current) && ReferenceEquals(current, session))
                _openEditors.Remove(key);
            session.Completion.TrySetResult(window.Result);
        };
        window.Closed += closedHandler;

        try
        {
            window.Show();
            Activate(window);
        }
        catch
        {
            window.Closed -= closedHandler;
            _openEditors.Remove(key);
            throw;
        }

        return session.Completion.Task;
    }

    private static void Activate(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }

    private sealed class EditorSession(CustomThemeWindow window)
    {
        public CustomThemeWindow Window { get; } = window;
        public TaskCompletionSource<CustomThemeEditResult?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public string? Rename(string currentName, IReadOnlyCollection<string> unavailableNames)
    {
        var window = new ThemeNameWindow(currentName, unavailableNames, localization)
        {
            Owner = Application.Current.MainWindow
        };
        return window.ShowDialog() == true ? window.Result : null;
    }
}
