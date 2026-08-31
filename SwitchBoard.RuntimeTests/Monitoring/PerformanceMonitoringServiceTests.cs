using SwitchBoard.Services.Monitoring;

namespace SwitchBoard.RuntimeTests.Monitoring;

[Collection("Windows runtime")]
public sealed class PerformanceMonitoringServiceTests
{
    [Theory]
    [InlineData("C:\\Games\\Steam.exe", "steam")]
    [InlineData("steam.exe", "steam")]
    [InlineData(" Steam ", "steam")]
    [InlineData(null, "")]
    [Trait("Category", "Unit")]
    public void NormalizeProcessName_UsesTheSameNameForPathsAndRunningProcesses(string? value, string expected)
    {
        Assert.Equal(expected, PerformanceMonitoringService.NormalizeProcessName(value));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CaptureAsync_ReturnsSnapshotsForLiveProcessesAndAllowsUnavailableCounters()
    {
        using var service = new PerformanceMonitoringService();
        var managed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var first = await service.CaptureAsync(managed, resetSamples: true);
        await Task.Delay(1100);
        var second = await service.CaptureAsync(managed);

        Assert.NotEmpty(first.Processes);
        Assert.NotEmpty(second.Processes);
        Assert.All(second.Processes, process => Assert.True(process.ProcessId > 0));
        Assert.Contains(second.Processes, process => process.ProcessId == Environment.ProcessId);
    }

    [Fact]
    [Trait("Category", "Regression")]
    public async Task CaptureAsync_SerializesConcurrentRefreshRequests()
    {
        using var service = new PerformanceMonitoringService();
        var managed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var first = service.CaptureAsync(managed, resetSamples: true);
        var second = service.CaptureAsync(managed);
        var snapshots = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, snapshots.Length);
        Assert.All(snapshots, snapshot => Assert.NotNull(snapshot.Processes));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Formatting_UsesSafePlaceholderForUnavailableCounters()
    {
        Assert.NotEmpty(PerformanceFormatting.Percent(null));
        Assert.NotEmpty(PerformanceFormatting.Rate(null));
        Assert.NotEmpty(PerformanceFormatting.Bytes(null));
    }

    [Fact]
    [Trait("Category", "Regression")]
    public void GpuSampler_UsesTheBusiestEngineInsteadOfSummingConcurrentEngines()
    {
        var source = File.ReadAllText(FindSourceFile("Services", "Monitoring", "PerformanceMonitoringService.cs"));

        Assert.Contains("engines.Values.Max()", source, StringComparison.Ordinal);
        Assert.Contains("group.Max(item => item.Value)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("engines.Values.Sum()", source, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Regression")]
    public void CaptureImplementation_PreservesProcessesWhenIndividualCountersAreUnavailable()
    {
        var source = File.ReadAllText(FindSourceFile("Services", "Monitoring", "PerformanceMonitoringService.cs"));

        Assert.Contains("TryReadCpuTime(process)", source, StringComparison.Ordinal);
        Assert.Contains("TryReadWorkingSet(process)", source, StringComparison.Ordinal);
        Assert.Contains("TryReadProcessName(process, id)", source, StringComparison.Ordinal);
    }

    private static string FindSourceFile(params string[] relativePath)
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. relativePath]);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException("Could not find the requested source file.", Path.Combine(relativePath));
    }
}
