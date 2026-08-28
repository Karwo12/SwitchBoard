using System.Text;

namespace SwitchBoard.Services.Windows;

public sealed record MonitorNameResolution(
    string DisplayName,
    string Source,
    string? DisplayConfigFriendlyName,
    string? DeviceFriendlyName,
    string? DeviceDescription,
    string? EdidProductName);

public static class MonitorNameResolver
{
    public static MonitorNameResolution Resolve(
        string? displayConfigFriendlyName,
        string? deviceFriendlyName,
        string? deviceDescription,
        string? edidProductName)
    {
        var displayConfig = Clean(displayConfigFriendlyName);
        var deviceName = Clean(deviceFriendlyName);
        var edidName = Clean(edidProductName);
        var description = Clean(deviceDescription);

        if (!IsGenericFallback(displayConfig))
            return new(displayConfig!, "DisplayConfigFriendlyName", displayConfig, deviceName, description, edidName);
        if (!IsGenericFallback(deviceName))
            return new(deviceName!, "DeviceFriendlyName", displayConfig, deviceName, description, edidName);
        if (!IsGenericFallback(edidName))
            return new(edidName!, "EdidProductName", displayConfig, deviceName, description, edidName);
        if (!IsGenericFallback(description))
            return new(description!, "DeviceDescription", displayConfig, deviceName, description, edidName);

        var fallback = displayConfig ?? deviceName ?? edidName ?? description;
        return new(fallback ?? "Generic PnP Monitor", "GenericFallback",
            displayConfig, deviceName, description, edidName);
    }

    public static string? ExtractEdidProductName(byte[]? edid)
    {
        if (edid is null || edid.Length < 128) return null;
        for (var descriptorOffset = 54; descriptorOffset + 18 <= Math.Min(edid.Length, 126); descriptorOffset += 18)
        {
            if (edid[descriptorOffset] != 0 || edid[descriptorOffset + 1] != 0 ||
                edid[descriptorOffset + 2] != 0 || edid[descriptorOffset + 3] != 0xFC)
                continue;

            var value = Encoding.ASCII.GetString(edid, descriptorOffset + 5, 13);
            value = new string(value.Where(character => character is not '\0' and not '\r' and not '\n').ToArray());
            value = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        return null;
    }

    public static bool IsGenericFallback(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        var normalized = value.Trim();
        return normalized.Equals("Generic PnP Monitor", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("Generic Monitor", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("PnP Monitor", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("Generic ", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = string.Join(' ', value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }
}
