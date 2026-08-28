using System.Drawing;
using System.Windows.Forms;

namespace SwitchBoard.Services.Tray;

/// <summary>Owns exactly one NotifyIcon for the running application.</summary>
public sealed class SystemTrayService : IDisposable
{
    private readonly ITrayIcon _trayIcon;
    private readonly ContextMenuStrip _menu;
    private readonly Action _open;
    private readonly Action _exit;
    private readonly Func<IReadOnlyList<TrayProfileShortcut>> _profiles;
    private readonly Func<bool> _canRestore;
    private readonly Action<Guid> _runProfile;
    private readonly Action _restore;
    private readonly Func<string, string>? _text;
    private bool _disposed;

    public SystemTrayService(Action open, Action exit, Func<IReadOnlyList<TrayProfileShortcut>> profiles,
        Func<bool> canRestore, Action<Guid> runProfile, Action restore, Func<string, string>? text = null)
        : this(open, exit, profiles, canRestore, runProfile, restore, new WindowsTrayIcon(), text)
    {
    }

    internal SystemTrayService(Action open, Action exit, Func<IReadOnlyList<TrayProfileShortcut>> profiles,
        Func<bool> canRestore, Action<Guid> runProfile, Action restore, ITrayIcon trayIcon,
        Func<string, string>? text = null)
    {
        _open = open;
        _exit = exit;
        _profiles = profiles;
        _canRestore = canRestore;
        _runProfile = runProfile;
        _restore = restore;
        _text = text;
        _trayIcon = trayIcon;
        _menu = new ContextMenuStrip();
        _menu.Opening += MenuOnOpening;
        _trayIcon.AttachMenu(_menu);
        _trayIcon.Visible = true;
        _trayIcon.DoubleClick += NotifyIconOnDoubleClick;
    }

    private void NotifyIconOnDoubleClick(object? sender, EventArgs e) => _open();

    private void MenuOnOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _menu.Items.Clear();
        _menu.Items.Add(Text("Tray.Open", "Open SwitchBoard"), null, (_, _) => _open());
        var shortcuts = _profiles().Take(12).ToList();
        if (shortcuts.Count > 0)
        {
            var profiles = new ToolStripMenuItem(Text("Tray.Profiles", "Profiles"));
            foreach (var shortcut in shortcuts)
            {
                var item = new ToolStripMenuItem(shortcut.Name);
                item.Click += (_, _) => _runProfile(shortcut.Id);
                profiles.DropDownItems.Add(item);
            }
            _menu.Items.Add(profiles);
        }
        if (_canRestore())
            _menu.Items.Add(Text("Tray.Restore", "Restore"), null, (_, _) => _restore());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Text("Tray.Exit", "Exit SwitchBoard"), null, (_, _) => _exit());
    }

    private string Text(string key, string fallback)
    {
        var value = _text?.Invoke(key);
        return string.IsNullOrWhiteSpace(value) || string.Equals(value, key, StringComparison.Ordinal) ? fallback : value;
    }

    public bool IsDisposed => _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _trayIcon.DoubleClick -= NotifyIconOnDoubleClick;
        _menu.Opening -= MenuOnOpening;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _menu.Dispose();
    }
}

public sealed record TrayProfileShortcut(Guid Id, string Name);

internal interface ITrayIcon : IDisposable
{
    event EventHandler? DoubleClick;
    bool Visible { get; set; }
    void AttachMenu(ContextMenuStrip menu);
}

internal sealed class WindowsTrayIcon : ITrayIcon
{
    private readonly NotifyIcon _icon = new()
    {
        Text = "SwitchBoard",
        Icon = LoadApplicationIcon()
    };

    public event EventHandler? DoubleClick
    {
        add => _icon.DoubleClick += value;
        remove => _icon.DoubleClick -= value;
    }

    public bool Visible { get => _icon.Visible; set => _icon.Visible = value; }
    public void AttachMenu(ContextMenuStrip menu) => _icon.ContextMenuStrip = menu;
    public void Dispose() => _icon.Dispose();

    private static Icon LoadApplicationIcon()
    {
        var executable = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executable))
        {
            try
            {
                if (Icon.ExtractAssociatedIcon(executable) is { } icon) return icon;
            }
            catch (ArgumentException) { }
        }
        return SystemIcons.Application;
    }
}
