using System.IO;
using SwitchBoard.Localization;

namespace SwitchBoard.Views;

public sealed record ArgumentPreset(string Id, string Flag, string DescriptionKey,
    string CompatibilityKey, bool RequiresValue = false, string? ValuePlaceholder = null,
    string? ApplicationKey = null);

public sealed class ArgumentPresetItem : System.ComponentModel.INotifyPropertyChanged
{
    private bool _isSelected;
    private string _value = string.Empty;
    private readonly ILocalizationService _localization;

    public ArgumentPresetItem(ArgumentPreset preset, ILocalizationService localization)
    {
        Preset = preset;
        _localization = localization;
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public ArgumentPreset Preset { get; }
    public string Flag => Preset.Flag;
    public string Description => _localization.GetString(Preset.DescriptionKey);
    public string Compatibility => _localization.GetString(Preset.CompatibilityKey);
    public string ApplicationKey => Preset.ApplicationKey ?? Preset.CompatibilityKey;
    public bool RequiresValue => Preset.RequiresValue;
    public string ValuePlaceholder => Preset.ValuePlaceholder ?? string.Empty;
    public bool IsSelected { get => _isSelected; set { if (_isSelected == value) return; _isSelected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); } }
    public string Value { get => _value; set { if (_value == value) return; _value = value; PropertyChanged?.Invoke(this, new(nameof(Value))); } }
}

public static class ArgumentPresetCatalog
{
    private static readonly IReadOnlyList<ArgumentPreset> Presets =
    [
        // Chromium-family switches are intentionally grouped because these applications share the Chromium launcher.
        new("chrome-minimized", "--start-minimized", "ArgumentPreset.Chrome.StartMinimized.Description", "ArgumentPreset.Chrome.StartMinimized.Compatibility", ApplicationKey: "chromium"),
        new("chrome-maximized", "--start-maximized", "ArgumentPreset.Chrome.StartMaximized.Description", "ArgumentPreset.Chrome.StartMaximized.Compatibility", ApplicationKey: "chromium"),
        new("chrome-incognito", "--incognito", "ArgumentPreset.Chrome.Incognito.Description", "ArgumentPreset.Chrome.Incognito.Compatibility", ApplicationKey: "chromium"),
        new("chrome-profile", "--profile-directory", "ArgumentPreset.Chrome.Profile.Description", "ArgumentPreset.Chrome.Profile.Compatibility", true, "Profile 1", "chromium"),
        new("chrome-app", "--app=", "ArgumentPreset.Chrome.App.Description", "ArgumentPreset.Chrome.App.Compatibility", true, "https://example.com", "chromium"),

        new("firefox-private-window", "-private-window", "ArgumentPreset.Firefox.PrivateWindow.Description", "ArgumentPreset.Firefox.Compatibility", ApplicationKey: "firefox"),
        new("firefox-new-window", "-new-window", "ArgumentPreset.Firefox.NewWindow.Description", "ArgumentPreset.Firefox.Compatibility", ApplicationKey: "firefox"),
        new("firefox-profile", "-P", "ArgumentPreset.Firefox.Profile.Description", "ArgumentPreset.Firefox.Compatibility", true, "Profile name", "firefox"),

        new("steam-silent", "-silent", "ArgumentPreset.Steam.Silent.Description", "ArgumentPreset.Steam.Compatibility", ApplicationKey: "steam"),
        new("steam-big-picture", "-bigpicture", "ArgumentPreset.Steam.BigPicture.Description", "ArgumentPreset.Steam.Compatibility", ApplicationKey: "steam"),
        new("steam-applaunch", "-applaunch", "ArgumentPreset.Steam.AppLaunch.Description", "ArgumentPreset.Steam.Compatibility", true, "730", "steam"),
        new("epic-opengl", "-OpenGL", "ArgumentPreset.Epic.OpenGl.Description", "ArgumentPreset.Epic.Compatibility", ApplicationKey: "epic"),
        new("epic-skip-build-prereq", "-SkipBuildPatchPrereq", "ArgumentPreset.Epic.SkipBuildPrereq.Description", "ArgumentPreset.Epic.Compatibility", ApplicationKey: "epic"),

        new("discord-minimized", "--start-minimized", "ArgumentPreset.Discord.StartMinimized.Description", "ArgumentPreset.Discord.Compatibility", ApplicationKey: "discord"),
        new("telegram-tray", "-startintray", "ArgumentPreset.Telegram.StartInTray.Description", "ArgumentPreset.Telegram.Compatibility", ApplicationKey: "telegram"),

        new("vlc-fullscreen", "--fullscreen", "ArgumentPreset.Vlc.Fullscreen.Description", "ArgumentPreset.Vlc.Compatibility", ApplicationKey: "vlc"),
        new("vlc-minimized", "--qt-start-minimized", "ArgumentPreset.Vlc.StartMinimized.Description", "ArgumentPreset.Vlc.Compatibility", ApplicationKey: "vlc"),
        new("vlc-no-title", "--no-video-title-show", "ArgumentPreset.Vlc.NoVideoTitle.Description", "ArgumentPreset.Vlc.Compatibility", ApplicationKey: "vlc"),

        new("obs-minimized", "--startminimized", "ArgumentPreset.OBS.StartMinimized.Description", "ArgumentPreset.OBS.Compatibility", ApplicationKey: "obs"),
        new("obs-recording", "--startrecording", "ArgumentPreset.OBS.StartRecording.Description", "ArgumentPreset.OBS.Compatibility", ApplicationKey: "obs"),
        new("obs-streaming", "--startstreaming", "ArgumentPreset.OBS.StartStreaming.Description", "ArgumentPreset.OBS.Compatibility", ApplicationKey: "obs"),
        new("obs-profile", "--profile", "ArgumentPreset.OBS.Profile.Description", "ArgumentPreset.OBS.Compatibility", true, "Profile name", "obs"),
        new("obs-collection", "--collection", "ArgumentPreset.OBS.Collection.Description", "ArgumentPreset.OBS.Compatibility", true, "Scene collection", "obs"),

        new("vscode-new-window", "--new-window", "ArgumentPreset.VsCode.NewWindow.Description", "ArgumentPreset.VsCode.Compatibility", ApplicationKey: "vscode"),
        new("vscode-reuse-window", "--reuse-window", "ArgumentPreset.VsCode.ReuseWindow.Description", "ArgumentPreset.VsCode.Compatibility", ApplicationKey: "vscode"),
        new("vscode-disable-extensions", "--disable-extensions", "ArgumentPreset.VsCode.DisableExtensions.Description", "ArgumentPreset.VsCode.Compatibility", ApplicationKey: "vscode"),
        new("vscode-user-data-dir", "--user-data-dir", "ArgumentPreset.VsCode.UserDataDir.Description", "ArgumentPreset.VsCode.Compatibility", true, "Directory", "vscode"),

        new("visual-studio-safe-mode", "/SafeMode", "ArgumentPreset.VisualStudio.SafeMode.Description", "ArgumentPreset.VisualStudio.Compatibility", ApplicationKey: "visualstudio"),
        new("visual-studio-no-splash", "/NoSplash", "ArgumentPreset.VisualStudio.NoSplash.Description", "ArgumentPreset.VisualStudio.Compatibility", ApplicationKey: "visualstudio"),
        new("terminal-maximized", "--maximized", "ArgumentPreset.Terminal.Maximized.Description", "ArgumentPreset.Terminal.Compatibility", ApplicationKey: "terminal"),
        new("terminal-fullscreen", "--fullscreen", "ArgumentPreset.Terminal.Fullscreen.Description", "ArgumentPreset.Terminal.Compatibility", ApplicationKey: "terminal"),
        new("terminal-profile", "--profile", "ArgumentPreset.Terminal.Profile.Description", "ArgumentPreset.Terminal.Compatibility", true, "Profile name", "terminal"),
        new("powershell-no-profile", "-NoProfile", "ArgumentPreset.PowerShell.NoProfile.Description", "ArgumentPreset.PowerShell.Compatibility", ApplicationKey: "powershell"),
        new("powershell-non-interactive", "-NonInteractive", "ArgumentPreset.PowerShell.NonInteractive.Description", "ArgumentPreset.PowerShell.Compatibility", ApplicationKey: "powershell"),
        new("powershell-window-style", "-WindowStyle", "ArgumentPreset.PowerShell.WindowStyle.Description", "ArgumentPreset.PowerShell.Compatibility", true, "Hidden", "powershell"),
        new("powershell-command", "-Command", "ArgumentPreset.PowerShell.Command.Description", "ArgumentPreset.PowerShell.Compatibility", true, "& { ... }", "powershell")
    ];

    public static IReadOnlyList<ArgumentPreset> ForTarget(string? target)
    {
        var preferred = GetApplicationKey(target);
        return Presets
            .OrderByDescending(preset => preferred is not null &&
                                         string.Equals(preset.ApplicationKey, preferred, StringComparison.OrdinalIgnoreCase))
            .ThenBy(preset => preset.CompatibilityKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(preset => preset.Flag, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<string> ApplicationKeys =>
        Presets.Select(preset => preset.ApplicationKey ?? string.Empty)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static bool IsPreferred(ArgumentPreset preset, string? target) =>
        GetApplicationKey(target) is { } key &&
        string.Equals(preset.ApplicationKey, key, StringComparison.OrdinalIgnoreCase);

    private static string? GetApplicationKey(string? target)
    {
        if ((target ?? string.Empty).StartsWith("steam://", StringComparison.OrdinalIgnoreCase)) return "steam";
        string name;
        try { name = Path.GetFileNameWithoutExtension(target ?? string.Empty).ToLowerInvariant(); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        { return null; }
        return name switch
        {
            "chrome" or "chromium" or "msedge" or "edge" or "brave" or "opera" or "opera_gx" => "chromium",
            "firefox" => "firefox",
            "steam" => "steam",
            "epicgameslauncher" or "epicgameslauncher-win64-shipping" => "epic",
            "discord" or "discordcanary" or "discordptb" => "discord",
            "telegram" or "telegramdesktop" => "telegram",
            "vlc" => "vlc",
            "obs" or "obs64" => "obs",
            "code" or "code-insiders" => "vscode",
            "devenv" => "visualstudio",
            "wt" or "windowsterminal" => "terminal",
            "powershell" or "pwsh" => "powershell",
            _ => null
        };
    }
}

public static class ArgumentComposer
{
    public static string Merge(string existing, IEnumerable<ArgumentPresetItem> selected)
    {
        var result = existing?.Trim() ?? string.Empty;
        foreach (var item in selected.Where(item => item.IsSelected))
        {
            var value = item.RequiresValue ? item.Value.Trim() : string.Empty;
            if (item.RequiresValue && string.IsNullOrWhiteSpace(value)) continue;
            var token = item.RequiresValue
                ? item.Flag.Contains("://", StringComparison.Ordinal) ? item.Flag + value : item.Flag + " " + QuoteIfNeeded(value)
                : item.Flag;
            var key = item.Flag.TrimEnd('=');
            if (ContainsArgument(result, key)) continue;
            result = string.IsNullOrWhiteSpace(result) ? token : result + " " + token;
        }
        return result;
    }

    private static bool ContainsArgument(string text, string key) =>
        text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(token => token.Trim('"').Equals(key, StringComparison.OrdinalIgnoreCase) ||
                         token.Trim('"').StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));

    private static string QuoteIfNeeded(string value) => value.Contains(' ') || value.Contains('"')
        ? "\"" + value.Replace("\"", "\\\"") + "\"" : value;
}

public static class ArgumentPresetFilter
{
    public static bool Matches(ArgumentPresetItem item, string? search, string? applicationFilter,
        string allApplicationsLabel)
    {
        var filter = search?.Trim() ?? string.Empty;
        var matchesSearch = string.IsNullOrWhiteSpace(filter) ||
                            item.Flag.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
                            item.Description.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
                            item.Compatibility.Contains(filter, StringComparison.CurrentCultureIgnoreCase);
        var matchesApplication = string.IsNullOrWhiteSpace(applicationFilter) ||
                                 string.Equals(applicationFilter, allApplicationsLabel,
                                     StringComparison.CurrentCultureIgnoreCase) ||
                                 string.Equals(item.Compatibility, applicationFilter,
                                     StringComparison.CurrentCultureIgnoreCase);
        return matchesSearch && matchesApplication;
    }
}
