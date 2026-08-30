namespace SwitchBoard.RuntimeTests.Monitoring;

public sealed class PerformancePanelCompositionTests
{
    [Fact]
    [Trait("Category", "Regression")]
    public void PerformancePanel_UsesOnePageScrollerAndAllResourceColumns()
    {
        var source = File.ReadAllText(FindSourceFile("Views", "Panels", "PerformancePanel.xaml"));

        Assert.Equal(1, source.Split("<ScrollViewer", StringSplitOptions.None).Length - 1);
        foreach (var key in new[] { "Performance.Name", "Performance.Cpu", "Performance.Memory", "Performance.Disk", "Performance.Network", "Performance.Gpu", "Performance.Vram" })
        {
            Assert.Contains(key, source, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("MaxHeight=\"310\"", source, StringComparison.Ordinal);
        Assert.Contains("ToggleLiveViewCommand", source, StringComparison.Ordinal);
        Assert.Contains("LiveViewToggleText", source, StringComparison.Ordinal);
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
