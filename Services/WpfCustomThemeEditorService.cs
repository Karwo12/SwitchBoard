using System.Windows;
using System.Windows.Threading;
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

        var previewScheduler = new ThemePreviewScheduler(request.ApplyTemporary, request.Colors,
            Application.Current.Dispatcher);
        var window = new CustomThemeWindow(request with { ApplyTemporary = previewScheduler.Queue }, paths, localization)
        {
            Owner = Application.Current.MainWindow
        };
        var session = new EditorSession(window);
        _openEditors.Add(key, session);

        EventHandler? closedHandler = null;
        closedHandler = (_, _) =>
        {
            window.Closed -= closedHandler;
            previewScheduler.Dispose();
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
            previewScheduler.Dispose();
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

    /// <summary>
    /// Coalesces high-frequency slider/color-picker changes. Asset switches stay synchronous,
    /// because closing the old media immediately is required before temporary files are removed.
    /// </summary>
    internal sealed class ThemePreviewScheduler : IDisposable
    {
        private readonly Action<CustomThemeSettings>? _apply;
        private readonly DispatcherTimer _timer;
        private CustomThemeSettings? _pending;
        private string _backgroundIdentity;
        private bool _disposed;

        public ThemePreviewScheduler(Action<CustomThemeSettings>? apply, CustomThemeSettings initial,
            Dispatcher dispatcher)
        {
            _apply = apply;
            _backgroundIdentity = GetBackgroundIdentity(initial);
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background,
                OnTick, dispatcher);
            _timer.Stop();
        }

        internal bool HasPendingUpdate => _pending is not null;

        public void Queue(CustomThemeSettings settings)
        {
            if (_disposed || _apply is null) return;
            var snapshot = settings.Clone();
            var identity = GetBackgroundIdentity(snapshot);
            if (!string.Equals(identity, _backgroundIdentity, StringComparison.OrdinalIgnoreCase))
            {
                _timer.Stop();
                _pending = null;
                _backgroundIdentity = identity;
                _apply(snapshot);
                return;
            }

            _pending = snapshot;
            if (!_timer.IsEnabled) _timer.Start();
        }

        internal void Flush()
        {
            _timer.Stop();
            var pending = _pending;
            _pending = null;
            if (!_disposed && pending is not null) _apply?.Invoke(pending);
        }

        private void OnTick(object? sender, EventArgs e) => Flush();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Stop();
            _timer.Tick -= OnTick;
            _pending = null;
        }

        private static string GetBackgroundIdentity(CustomThemeSettings settings) =>
            $"{settings.BackgroundAssetFileName}\n{settings.PreviewBackgroundPath}";
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
