namespace SwitchBoard.Controls;

/// <summary>
/// Native pixel dimensions reported once by the active background renderer.
/// They deliberately describe the source media, not its stretched WPF presentation.
/// </summary>
public readonly record struct BackgroundNativeSize(string SourcePath, int PixelWidth, int PixelHeight)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(SourcePath) && PixelWidth > 0 && PixelHeight > 0;
}

public sealed class BackgroundNativeSizeChangedEventArgs(BackgroundNativeSize size) : EventArgs
{
    public BackgroundNativeSize Size { get; } = size;
}

internal sealed class BackgroundNativeSizeCache
{
    public BackgroundNativeSize? Current { get; private set; }

    public bool TryUpdate(BackgroundNativeSize size)
    {
        if (!size.IsValid || Current is BackgroundNativeSize current && current == size) return false;
        Current = size;
        return true;
    }

    public void ClearWhenSourceChanges(string? sourcePath)
    {
        if (Current is not BackgroundNativeSize current || BackgroundSourcePath.Equals(current.SourcePath, sourcePath)) return;
        Current = null;
    }

    public void Clear() => Current = null;
}
