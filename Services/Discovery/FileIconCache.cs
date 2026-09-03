using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Collections.Concurrent;
using System.IO;
using System.Resources;

namespace SwitchBoard.Services.Discovery;

/// <summary>
/// Shared, bounded cache for shell icons. Icon extraction is always performed off the UI thread
/// and every cached bitmap is frozen by <see cref="FileIconProvider"/> before it reaches WPF.
/// </summary>
public sealed class FileIconCache
{
    private const int DefaultCapacity = 256;
    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _recency = [];
    // Shell HICON extraction and WPF's HICON conversion are not reliable when entered
    // concurrently. Keep this gate static so every consumer (profiles, action cards and
    // Performance) uses the same native extraction lane; the UI still only awaits it.
    private static readonly SemaphoreSlim ExtractionLoadLimiter = new(1, 1);
    private static readonly ConcurrentDictionary<ActionIconAsset, Lazy<ImageSource?>> ActionIconSources = new();
    private static readonly ResourceManager ActionIconResources = new("SwitchBoard.g", typeof(FileIconCache).Assembly);
    private readonly int _capacity;
    private readonly Func<string?, ImageSource?> _loader;

    public FileIconCache(int capacity = DefaultCapacity, Func<string?, ImageSource?>? loader = null)
    {
        _capacity = Math.Max(1, capacity);
        _loader = loader ?? FileIconProvider.TryGetSmallIcon;
    }

    public static FileIconCache Shared { get; } = new();

    /// <summary>
    /// Retrieves one of the packaged, immutable fallback icons. The same frozen source is shared
    /// by every action card, just like file-backed EXE icons are shared by this cache.
    /// </summary>
    internal ImageSource? GetActionIcon(ActionIconAsset asset)
    {
        var source = ActionIconSources.GetOrAdd(asset,
            static value => new Lazy<ImageSource?>(() => LoadActionIcon(value))).Value;
        // Do not permanently cache a transient resource-loading failure (for example while a
        // WPF Application is shutting down in a test host). Successful sources stay frozen and
        // shared for the process lifetime.
        if (source is null) ActionIconSources.TryRemove(asset, out _);
        return source;
    }

    internal int CachedEntryCount
    {
        get
        {
            lock (_gate) return _entries.Count;
        }
    }

    /// <summary>
    /// Returns a cached icon when it is already known (including a cached missing icon).
    /// It never triggers file IO or extraction.
    /// </summary>
    public bool TryGetCachedIcon(string? sourcePath, out ImageSource? icon)
    {
        icon = null;
        var key = NormalizePath(sourcePath);
        if (key is null) return false;

        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry) || !entry.IconTask.IsCompletedSuccessfully)
                return false;

            Touch(entry);
            icon = entry.IconTask.Result;
            return true;
        }
    }

    /// <summary>
    /// Resolves an EXE or ICO once per canonical path. Cancellation only stops the
    /// caller's wait; it never cancels a shared extraction another card may be awaiting.
    /// </summary>
    public Task<ImageSource?> GetSmallIconAsync(string? sourcePath, CancellationToken cancellationToken = default)
    {
        var key = NormalizePath(sourcePath);
        if (key is null) return Task.FromResult<ImageSource?>(null);

        Task<ImageSource?> task;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                Touch(entry);
                task = entry.IconTask;
            }
            else
            {
                task = LoadIconAsync(key);
                var node = _recency.AddLast(key);
                _entries[key] = new CacheEntry(task, node);
                TrimToCapacity();
            }
        }

        return cancellationToken.CanBeCanceled ? task.WaitAsync(cancellationToken) : task;
    }

    private async Task<ImageSource?> LoadIconAsync(string path)
    {
        await ExtractionLoadLimiter.WaitAsync().ConfigureAwait(false);
        try
        {
            // Do not ask the shell for a generic file-type icon when the source has gone away.
            return await Task.Run(() => File.Exists(path) ? _loader(path) : null).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            ExtractionLoadLimiter.Release();
        }
    }

    private void Touch(CacheEntry entry)
    {
        _recency.Remove(entry.RecencyNode);
        _recency.AddLast(entry.RecencyNode);
    }

    private void TrimToCapacity()
    {
        while (_entries.Count > _capacity && _recency.First is { } oldest)
        {
            _recency.RemoveFirst();
            _entries.Remove(oldest.Value);
        }
    }

    private static string? NormalizePath(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return null;

        try
        {
            var trimmed = sourcePath.Trim();
            if (!Path.IsPathFullyQualified(trimmed)) return null;
            var fullPath = Path.GetFullPath(trimmed);
            var extension = Path.GetExtension(fullPath);
            return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".ico", StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static ImageSource? LoadActionIcon(ActionIconAsset asset)
    {
        try
        {
            // Read directly from the assembly's WPF resource table. Unlike a pack URI this
            // stays available when a host is tearing down Application, while still working
            // after publish without any machine-local asset path.
            using var stream = ActionIconResources.GetObject($"assets/actionicons/{GetActionIconFileName(asset)}.png")
                as Stream;
            if (stream is null) return null;
            var source = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad).Frames[0];
            source.Freeze();
            return source;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string GetActionIconFileName(ActionIconAsset asset) => asset switch
    {
        ActionIconAsset.Audio => "audio",
        ActionIconAsset.Command => "cmd",
        ActionIconAsset.Condition => "condition",
        ActionIconAsset.Delay => "delay",
        ActionIconAsset.Device => "device",
        ActionIconAsset.Display => "display",
        ActionIconAsset.Power => "power",
        ActionIconAsset.PowerShell => "powershell",
        ActionIconAsset.Process => "process",
        ActionIconAsset.Script => "script",
        ActionIconAsset.Service => "service",
        _ => "fallback"
    };

    private sealed record CacheEntry(Task<ImageSource?> IconTask, LinkedListNode<string> RecencyNode);
}

internal enum ActionIconAsset
{
    Fallback,
    Audio,
    Command,
    Condition,
    Delay,
    Device,
    Display,
    Power,
    PowerShell,
    Process,
    Script,
    Service
}
