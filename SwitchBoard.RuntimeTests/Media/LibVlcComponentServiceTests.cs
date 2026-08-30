using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using SwitchBoard.Services.Media;

namespace SwitchBoard.RuntimeTests.Media;

public sealed class LibVlcComponentServiceTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task InstallAsync_VerifiedPackageIsAtomicallyInstalledAndCanBeRemoved()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var package = CreatePackage();
            using var client = CreateClient(package, ComputeSha256(package));
            var paths = new AppDataPaths(root);
            var service = new LibVlcComponentService(paths, client, CreateDescriptor());

            var installed = await service.InstallAsync();

            Assert.Equal(LibVlcComponentState.Installed, installed.State);
            Assert.True(File.Exists(Path.Combine(paths.LibVlcComponentDirectory,
                LibVlcComponentDescriptor.EntryAssemblyName)));
            Assert.True(Directory.Exists(Path.Combine(paths.LibVlcComponentDirectory, "plugins")));
            Assert.Empty(Directory.GetDirectories(paths.OptionalComponentsDirectory, ".libvlc-stage-*"));

            var removed = await service.RemoveAsync();

            Assert.Equal(LibVlcComponentState.NotInstalled, removed.State);
            Assert.False(Directory.Exists(paths.LibVlcComponentDirectory));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InstallAsync_ChecksumFailureDoesNotCreateAnInstalledComponent()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var package = CreatePackage();
            using var client = CreateClient(package, new string('0', 64));
            var paths = new AppDataPaths(root);
            var service = new LibVlcComponentService(paths, client, CreateDescriptor());

            await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallAsync());

            Assert.Equal(LibVlcComponentState.NotInstalled, service.GetStatus().State);
            Assert.False(Directory.Exists(paths.LibVlcComponentDirectory));
            Assert.False(Directory.Exists(paths.OptionalComponentsDirectory) &&
                         Directory.GetDirectories(paths.OptionalComponentsDirectory, ".libvlc-stage-*").Length > 0);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InstallAsync_UnsafeArchivePathDoesNotEscapeOrInstallAnything()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var package = CreatePackage(includeUnsafeEntry: true);
            using var client = CreateClient(package, ComputeSha256(package));
            var paths = new AppDataPaths(root);
            var service = new LibVlcComponentService(paths, client, CreateDescriptor());

            await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallAsync());

            Assert.False(Directory.Exists(paths.LibVlcComponentDirectory));
            Assert.False(File.Exists(Path.Combine(paths.OptionalComponentsDirectory, "unexpected.txt")));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InstallAsync_RejectsNonHttpsComponentSourcesBeforeDownloading()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var descriptor = CreateDescriptor("http://example.test/component.zip", "https://example.test/component.sha256");
            using var client = new HttpClient(new StaticHandler(_ => throw new InvalidOperationException("No request expected.")));
            var service = new LibVlcComponentService(new AppDataPaths(root), client, descriptor);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallAsync());
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PluginLoader_InvalidOptionalAssemblyFailsWithoutAffectingTheBaseApp()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var package = CreatePackage();
            using var client = CreateClient(package, ComputeSha256(package));
            var paths = new AppDataPaths(root);
            var service = new LibVlcComponentService(paths, client, CreateDescriptor());
            await service.InstallAsync();
            var loader = new LibVlcPluginLoader(service);

            var created = loader.TryCreateRenderer(out var renderer, out var error);

            Assert.False(created);
            Assert.Null(renderer);
            Assert.NotNull(error);
            Assert.True(service.GetStatus().IsInstalled);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    private static LibVlcComponentDescriptor CreateDescriptor(
        string packageUri = "https://example.test/component.zip",
        string checksumUri = "https://example.test/component.sha256") => new(
        LibVlcComponentDescriptor.PinnedComponentVersion,
        new Uri(packageUri),
        new Uri(checksumUri),
        1024);

    private static HttpClient CreateClient(byte[] package, string checksum)
    {
        var responses = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://example.test/component.zip"] = package,
            ["https://example.test/component.sha256"] = Encoding.ASCII.GetBytes($"{checksum} *component.zip")
        };
        return new HttpClient(new StaticHandler(request =>
        {
            var address = request.RequestUri?.AbsoluteUri ?? string.Empty;
            if (!responses.TryGetValue(address, out var payload)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            };
        }));
    }

    private static byte[] CreatePackage(bool includeUnsafeEntry = false)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "ComponentManifest.json", """
                {
                  "schemaVersion": 1,
                  "componentId": "libvlc",
                  "componentVersion": "3.0.23.1-3.10.1",
                  "entryAssembly": "SwitchBoard.LibVlcPlugin.dll",
                  "entryType": "SwitchBoard.LibVlcPlugin.LibVlcVideoBackgroundRendererFactory",
                  "libVlcVersion": "3.0.23.1",
                  "libVlcSharpVersion": "3.10.1"
                }
                """);
            foreach (var entry in new[]
                     {
                         "SwitchBoard.LibVlcPlugin.dll", "SwitchBoard.LibVlcPlugin.deps.json", "LibVLCSharp.dll",
                         "LibVLCSharp.WPF.dll", "libvlc.dll", "libvlccore.dll", "plugins/codec.dll"
                     })
                WriteEntry(archive, entry, "test");
            if (includeUnsafeEntry) WriteEntry(archive, "../unexpected.txt", "unsafe");
        }
        return stream.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private static string ComputeSha256(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"SwitchBoard-libvlc-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTemporaryRoot(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private sealed class StaticHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
