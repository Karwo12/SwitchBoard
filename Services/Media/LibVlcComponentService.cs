using System.IO.Compression;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using SwitchBoard.Data;

namespace SwitchBoard.Services.Media;

/// <summary>
/// Describes one deliberately pinned companion component. The application never
/// asks GitHub for a "latest" package: its own release version and these engine
/// versions determine the only acceptable asset names.
/// </summary>
public sealed record LibVlcComponentDescriptor(
    string ComponentVersion,
    Uri PackageUri,
    Uri ChecksumUri,
    long ApproximateDownloadBytes)
{
    public const string ComponentId = "libvlc";
    public const string EntryAssemblyName = "SwitchBoard.LibVlcPlugin.dll";
    public const string EntryTypeName = "SwitchBoard.LibVlcPlugin.LibVlcVideoBackgroundRendererFactory";
    public const string PinnedLibVlcVersion = "3.0.23.1";
    public const string PinnedLibVlcSharpVersion = "3.10.1";
    public const string PinnedComponentVersion = PinnedLibVlcVersion + "-" + PinnedLibVlcSharpVersion;
    public const string PackageFileName = "SwitchBoard-LibVLC-3.0.23.1-3.10.1-win-x64.zip";
    public const string ChecksumFileName = "SwitchBoard-LibVLC-3.0.23.1-3.10.1-win-x64.sha256.txt";

    // The win-x64 runtime is about 101 MiB uncompressed. The companion ZIP made
    // from it is currently about 45 MiB, so present a stable, conservative size
    // rather than the much larger multi-architecture NuGet package size.
    public const long DefaultApproximateDownloadBytes = 50L * 1024 * 1024;

    public static LibVlcComponentDescriptor ForApplicationVersion(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var releaseTag = "v" + version.ToString(3);
        var baseUri = $"https://github.com/Karwo12/SwitchBoard/releases/download/{releaseTag}/";
        return new LibVlcComponentDescriptor(
            PinnedComponentVersion,
            new Uri(baseUri + PackageFileName),
            new Uri(baseUri + ChecksumFileName),
            DefaultApproximateDownloadBytes);
    }
}

public enum LibVlcComponentState
{
    NotInstalled,
    Installed,
    RemovalPending,
    Invalid
}

public sealed record LibVlcComponentStatus(LibVlcComponentState State, string ComponentDirectory, string? Detail = null)
{
    public bool IsInstalled => State == LibVlcComponentState.Installed;
}

public sealed record LibVlcInstallProgress(long BytesReceived, long? TotalBytes, string Stage);

public interface ILibVlcComponentService
{
    LibVlcComponentDescriptor Descriptor { get; }
    LibVlcComponentStatus GetStatus();
    Task<LibVlcComponentStatus> InstallAsync(IProgress<LibVlcInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<LibVlcComponentStatus> RemoveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Installs only the explicit LibVLC companion asset. Downloads and extraction
/// happen in a staging directory; the final directory receives a verified package
/// only after the SHA-256 and package manifest both pass validation.
/// </summary>
public sealed class LibVlcComponentService : ILibVlcComponentService
{
    private const int MaximumArchiveEntries = 5_000;
    private const long MaximumPackageBytes = 256L * 1024 * 1024;
    private const long MaximumUncompressedBytes = 512L * 1024 * 1024;
    private static readonly Regex Sha256Pattern = new(@"(?<![0-9a-fA-F])[0-9a-fA-F]{64}(?![0-9a-fA-F])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly AppDataPaths _paths;
    private readonly HttpClient _client;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private bool _runtimeWasLoaded;

    public LibVlcComponentService(AppDataPaths paths, HttpClient client, LibVlcComponentDescriptor descriptor)
    {
        _paths = paths;
        _client = client;
        Descriptor = descriptor;
        CompletePendingRemoval();
    }

    public LibVlcComponentDescriptor Descriptor { get; }

    public LibVlcComponentStatus GetStatus()
    {
        var componentDirectory = _paths.LibVlcComponentDirectory;
        if (File.Exists(_paths.LibVlcRemovalMarkerPath))
            return new(LibVlcComponentState.RemovalPending, componentDirectory);
        if (!Directory.Exists(componentDirectory))
            return new(LibVlcComponentState.NotInstalled, componentDirectory);
        return ValidateInstalledComponent(componentDirectory, out var detail)
            ? new(LibVlcComponentState.Installed, componentDirectory)
            : new(LibVlcComponentState.Invalid, componentDirectory, detail);
    }

    public async Task<LibVlcComponentStatus> InstallAsync(IProgress<LibVlcInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            var current = GetStatus();
            if (current.IsInstalled) return current;
            if (_runtimeWasLoaded)
                throw new InvalidOperationException("Restart SwitchBoard before installing or replacing the LibVLC component.");

            ValidateHttps(Descriptor.PackageUri);
            ValidateHttps(Descriptor.ChecksumUri);
            Directory.CreateDirectory(_paths.OptionalComponentsDirectory);
            var stagingRoot = Path.Combine(_paths.OptionalComponentsDirectory, $".libvlc-stage-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingRoot);
            string? previousDirectory = null;
            try
            {
                progress?.Report(new LibVlcInstallProgress(0, null, "checksum"));
                var expectedHash = await DownloadExpectedHashAsync(Descriptor.ChecksumUri, cancellationToken);
                var packagePath = Path.Combine(stagingRoot, "component.zip.partial");
                progress?.Report(new LibVlcInstallProgress(0, Descriptor.ApproximateDownloadBytes, "download"));
                await DownloadToFileAsync(Descriptor.PackageUri, packagePath, progress, cancellationToken);
                await VerifySha256Async(packagePath, expectedHash, cancellationToken);

                var extractedDirectory = Path.Combine(stagingRoot, "component");
                progress?.Report(new LibVlcInstallProgress(0, null, "extract"));
                ExtractArchive(packagePath, extractedDirectory);
                if (!ValidateInstalledComponent(extractedDirectory, out var detail))
                    throw new InvalidDataException($"The LibVLC package is incomplete or incompatible: {detail}");

                var destination = _paths.LibVlcComponentDirectory;
                if (Directory.Exists(destination))
                {
                    previousDirectory = Path.Combine(_paths.OptionalComponentsDirectory, $".libvlc-previous-{Guid.NewGuid():N}");
                    Directory.Move(destination, previousDirectory);
                }

                try
                {
                    Directory.Move(extractedDirectory, destination);
                    if (File.Exists(_paths.LibVlcRemovalMarkerPath)) File.Delete(_paths.LibVlcRemovalMarkerPath);
                }
                catch
                {
                    if (previousDirectory is not null && !Directory.Exists(destination) && Directory.Exists(previousDirectory))
                        Directory.Move(previousDirectory, destination);
                    throw;
                }

                if (previousDirectory is not null && Directory.Exists(previousDirectory))
                    Directory.Delete(previousDirectory, recursive: true);
                progress?.Report(new LibVlcInstallProgress(Descriptor.ApproximateDownloadBytes,
                    Descriptor.ApproximateDownloadBytes, "complete"));
                return GetStatus();
            }
            finally
            {
                if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true);
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<LibVlcComponentStatus> RemoveAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            var componentDirectory = _paths.LibVlcComponentDirectory;
            if (!Directory.Exists(componentDirectory))
            {
                if (File.Exists(_paths.LibVlcRemovalMarkerPath)) File.Delete(_paths.LibVlcRemovalMarkerPath);
                return GetStatus();
            }

            if (_runtimeWasLoaded)
            {
                Directory.CreateDirectory(_paths.OptionalComponentsDirectory);
                await File.WriteAllTextAsync(_paths.LibVlcRemovalMarkerPath, "remove after application restart", cancellationToken);
                return GetStatus();
            }

            Directory.Delete(componentDirectory, recursive: true);
            if (File.Exists(_paths.LibVlcRemovalMarkerPath)) File.Delete(_paths.LibVlcRemovalMarkerPath);
            return GetStatus();
        }
        finally
        {
            _operationLock.Release();
        }
    }

    internal void MarkRuntimeLoaded() => _runtimeWasLoaded = true;

    private void CompletePendingRemoval()
    {
        if (!File.Exists(_paths.LibVlcRemovalMarkerPath)) return;
        try
        {
            if (Directory.Exists(_paths.LibVlcComponentDirectory))
                Directory.Delete(_paths.LibVlcComponentDirectory, recursive: true);
            File.Delete(_paths.LibVlcRemovalMarkerPath);
        }
        catch
        {
            // Keep the marker. A later startup can retry rather than reporting a
            // component as removed while native files are still locked.
        }
    }

    private async Task<string> DownloadExpectedHashAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        EnsureSecureSuccess(response, uri);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        var match = Sha256Pattern.Match(text);
        if (!match.Success) throw new InvalidDataException("The LibVLC checksum file does not contain a SHA-256 value.");
        return match.Value.ToLowerInvariant();
    }

    private async Task DownloadToFileAsync(Uri uri, string destination, IProgress<LibVlcInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        EnsureSecureSuccess(response, uri);
        if (response.Content.Headers.ContentLength is > MaximumPackageBytes)
            throw new InvalidDataException("The LibVLC package exceeds the supported download size.");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 131_072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[131_072];
        long received = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            received += read;
            if (received > MaximumPackageBytes)
                throw new InvalidDataException("The LibVLC package exceeds the supported download size.");
            progress?.Report(new LibVlcInstallProgress(received, response.Content.Headers.ContentLength, "download"));
        }
        await target.FlushAsync(cancellationToken);
    }

    private static async Task VerifySha256Async(string path, string expectedHash, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 131_072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = await SHA256.HashDataAsync(stream, cancellationToken);
        var expected = Convert.FromHexString(expectedHash);
        if (expected.Length != actual.Length || !CryptographicOperations.FixedTimeEquals(actual, expected))
            throw new InvalidDataException("The downloaded LibVLC package did not match its SHA-256 checksum.");
    }

    private static void ExtractArchive(string packagePath, string destinationDirectory)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        if (archive.Entries.Count > MaximumArchiveEntries)
            throw new InvalidDataException("The LibVLC package contains too many archive entries.");
        if (archive.Entries.Sum(entry => entry.Length) > MaximumUncompressedBytes)
            throw new InvalidDataException("The LibVLC package is larger than the supported uncompressed limit.");

        Directory.CreateDirectory(destinationDirectory);
        var fullDestination = Path.GetFullPath(destinationDirectory) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var target = Path.GetFullPath(Path.Combine(destinationDirectory, entry.FullName));
            if (!target.StartsWith(fullDestination, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The LibVLC package contains an unsafe archive path.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
        }
    }

    private static void ValidateHttps(Uri uri)
    {
        if (!uri.IsAbsoluteUri || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The LibVLC component source must use HTTPS.");
    }

    private static void EnsureSecureSuccess(HttpResponseMessage response, Uri originalUri)
    {
        if (response.RequestMessage?.RequestUri is { } finalUri) ValidateHttps(finalUri);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Could not download the LibVLC component from {originalUri.Host}: {(int)response.StatusCode}.");
    }

    private bool ValidateInstalledComponent(string componentDirectory, out string? detail)
    {
        var requiredPaths = new[]
        {
            "ComponentManifest.json",
            LibVlcComponentDescriptor.EntryAssemblyName,
            "SwitchBoard.LibVlcPlugin.deps.json",
            "LibVLCSharp.dll",
            "LibVLCSharp.WPF.dll",
            "libvlc.dll",
            "libvlccore.dll"
        };
        foreach (var required in requiredPaths)
        {
            if (!File.Exists(Path.Combine(componentDirectory, required)))
            {
                detail = $"Missing {required}.";
                return false;
            }
        }
        if (!Directory.Exists(Path.Combine(componentDirectory, "plugins")))
        {
            detail = "Missing the VLC plugins directory.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(componentDirectory, "ComponentManifest.json")));
            var root = document.RootElement;
            if (!root.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 1 ||
                !HasProperty(root, "componentId", LibVlcComponentDescriptor.ComponentId) ||
                !HasProperty(root, "componentVersion", Descriptor.ComponentVersion) ||
                !HasProperty(root, "entryAssembly", LibVlcComponentDescriptor.EntryAssemblyName) ||
                !HasProperty(root, "entryType", LibVlcComponentDescriptor.EntryTypeName) ||
                !HasProperty(root, "libVlcVersion", LibVlcComponentDescriptor.PinnedLibVlcVersion) ||
                !HasProperty(root, "libVlcSharpVersion", LibVlcComponentDescriptor.PinnedLibVlcSharpVersion))
            {
                detail = "The component manifest does not match the pinned LibVLC version.";
                return false;
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            detail = exception.Message;
            return false;
        }

        detail = null;
        return true;
    }

    private static bool HasProperty(JsonElement root, string property, string expected) =>
        root.TryGetProperty(property, out var value) &&
        string.Equals(value.GetString(), expected, StringComparison.Ordinal);
}
