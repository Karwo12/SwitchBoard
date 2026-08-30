namespace SwitchBoard.Data;

/// <summary>
/// Stable persisted identifiers for the MP4 renderer selector.  The default is
/// deliberately Automatic so existing installations continue to use WPF's
/// MediaPlayer unless that backend fails and the user has installed LibVLC.
/// </summary>
public static class Mp4RendererPreferences
{
    public const string Automatic = "automatic";
    public const string WindowsMediaPlayer = "windowsMediaPlayer";
    public const string LibVlc = "libvlc";

    public static string Normalize(string? value) => value?.Trim() switch
    {
        var candidate when string.Equals(candidate, WindowsMediaPlayer, StringComparison.OrdinalIgnoreCase) =>
            WindowsMediaPlayer,
        var candidate when string.Equals(candidate, LibVlc, StringComparison.OrdinalIgnoreCase) => LibVlc,
        _ => Automatic
    };
}
