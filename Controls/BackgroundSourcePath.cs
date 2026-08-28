using System.IO;

namespace SwitchBoard.Controls;

internal static class BackgroundSourcePath
{
    public static string? NormalizeExisting(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var fullPath = Path.GetFullPath(path);
            return File.Exists(fullPath) ? fullPath : null;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
                                          NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static bool Equals(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
