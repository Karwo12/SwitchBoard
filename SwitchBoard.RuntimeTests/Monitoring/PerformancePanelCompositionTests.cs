namespace SwitchBoard.RuntimeTests.Monitoring;

public sealed class PerformancePanelCompositionTests
{
    [Fact]
    [Trait("Category", "Regression")]
    public void PerformancePanel_UsesOnePageScrollerAndVirtualizedProcessRows()
    {
        var source = File.ReadAllText(FindSourceFile("Views", "Panels", "PerformancePanel.xaml"));

        Assert.Equal(0, source.Split("<ScrollViewer", StringSplitOptions.None).Length - 1);
        foreach (var key in new[] { "Performance.Name", "Performance.Cpu", "Performance.Memory", "Performance.Disk", "Performance.Gpu", "Performance.Vram" })
        {
            Assert.Contains(key, source, StringComparison.Ordinal);
        }

        Assert.Contains("VirtualizingPanel.IsVirtualizing=\"True\"", source, StringComparison.Ordinal);
        Assert.Contains("VirtualizingPanel.VirtualizationMode=\"Recycling\"", source, StringComparison.Ordinal);
        Assert.Contains("ScrollViewer.VerticalScrollBarVisibility=\"Auto\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Performance.ProcessLimitHint", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Performance.SwitchBoardImpact", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SwitchBoardCpuText", source, StringComparison.Ordinal);
        Assert.Contains("ToggleLiveViewCommand", source, StringComparison.Ordinal);
        Assert.Contains("LiveViewToggleText", source, StringComparison.Ordinal);
        foreach (var key in new[]
                 {
                     "Performance.Measurement.CpuAverage", "Performance.Measurement.CpuPeak",
                     "Performance.Measurement.MemoryPeak", "Performance.Measurement.DiskTotal",
                     "Performance.Measurement.GpuAverage", "Performance.Measurement.VramPeak"
                 })
        {
            Assert.Contains(key, source, StringComparison.Ordinal);
        }
        Assert.Contains("PeakVramText", source, StringComparison.Ordinal);
        Assert.Contains("IsSortCpuActive", source, StringComparison.Ordinal);
    }

    private static string FindSourceFile(params string[] relativePath)
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. relativePath]);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException("Could not find the performance panel XAML.");
    }
}
