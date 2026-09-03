using System.Runtime.InteropServices;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SwitchBoard.Services.Discovery;

internal static class FileIconProvider
{
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiSmallIcon = 0x000000001;
    private static readonly object NativeExtractionGate = new();

    public static ImageSource? TryGetSmallIcon(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        // The shell HICON APIs and WPF's HICON conversion sporadically return no bitmap
        // under concurrent calls. This provider is also used by the existing discovery
        // flows, so the gate needs to live here rather than in a caller-specific path.
        lock (NativeExtractionGate)
        {
            return TryGetSmallIconCore(path);
        }
    }

    private static ImageSource? TryGetSmallIconCore(string path)
    {
        var info = new ShellFileInfo();
        try
        {
            // SHGetFileInfo silently substitutes Windows' generic executable icon for
            // binaries without an icon resource. Let the caller render its own
            // intentional packaged fallback instead of displaying that system fallback.
            if (string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) &&
                ExtractIconEx(path, -1, IntPtr.Zero, IntPtr.Zero, 0) == 0)
            {
                return null;
            }

            var result = SHGetFileInfo(
                path,
                0,
                ref info,
                (uint)Marshal.SizeOf<ShellFileInfo>(),
                ShgfiIcon | ShgfiSmallIcon);
            if (result == IntPtr.Zero || info.IconHandle == IntPtr.Zero)
            {
                return null;
            }

            var source = Imaging.CreateBitmapSourceFromHIcon(
                info.IconHandle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(20, 20));
            source.Freeze();
            return source;
        }
        catch (Exception exception) when (exception is ArgumentException or ExternalException)
        {
            return null;
        }
        finally
        {
            if (info.IconHandle != IntPtr.Zero)
            {
                DestroyIcon(info.IconHandle);
            }
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path,
        uint fileAttributes,
        ref ShellFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(
        string file,
        int iconIndex,
        IntPtr largeIcon,
        IntPtr smallIcon,
        uint iconCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr iconHandle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr IconHandle;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }
}
