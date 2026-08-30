namespace SwitchBoard.RuntimeTests.Views;

public sealed class SystemAndPerformanceNavigationTests
{
    [Fact]
    [Trait("Category", "Regression")]
    public void NewPanels_ReuseTheExistingNavigationAndCardVocabulary()
    {
        var path = FindSourceFile("Views", "MainWindow.xaml");
        var xaml = File.ReadAllText(path);

        Assert.Contains("x:Name=\"SystemNavigationButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PerformanceNavigationButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SystemContent\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PerformanceContent\"", xaml, StringComparison.Ordinal);
        Assert.True(xaml.Split("Style=\"{StaticResource TopNavigationButtonStyle}\"").Length - 1 >= 5);

        var systemStart = xaml.IndexOf("x:Name=\"SystemContent\"", StringComparison.Ordinal);
        var performanceStart = xaml.IndexOf("x:Name=\"PerformanceContent\"", StringComparison.Ordinal);
        var settingsStart = xaml.IndexOf("x:Name=\"SettingsWorkspace\"", StringComparison.Ordinal);
        Assert.True(systemStart >= 0 && performanceStart > systemStart && settingsStart > performanceStart);
        Assert.Contains("<panels:SystemPanel", xaml, StringComparison.Ordinal);
        Assert.Contains("<panels:PerformancePanel", xaml, StringComparison.Ordinal);

        var panels = string.Concat(
            File.ReadAllText(FindSourceFile("Views", "Panels", "SystemPanel.xaml")),
            File.ReadAllText(FindSourceFile("Views", "Panels", "PerformancePanel.xaml")),
            File.ReadAllText(FindSourceFile("Views", "Panels", "ActivityPanel.xaml")));
        Assert.Contains("Background=\"{DynamicResource ActivitySurfaceBrush}\"", panels, StringComparison.Ordinal);
        Assert.True(
            panels.Contains("Style=\"{StaticResource CardSurfaceStyle}\"", StringComparison.Ordinal) ||
            panels.Contains("BasedOn=\"{StaticResource CardSurfaceStyle}\"", StringComparison.Ordinal),
            "Panels should use or derive from the shared card surface style.");
        foreach (var windowOwnedStyle in new[]
                 {
                     "ActivityRowSurfaceStyle", "ActivityHistoryRowSurfaceStyle",
                     "ActivityHistoryHeaderButtonStyle", "ProfileResultStatusDotStyle",
                     "ActionResultStatusDotStyle"
                 })
        {
            Assert.DoesNotContain($"{{StaticResource {windowOwnedStyle}}}", panels, StringComparison.Ordinal);
            Assert.Contains($"{{DynamicResource {windowOwnedStyle}}}", panels, StringComparison.Ordinal);
        }
        Assert.Contains("HorizontalScrollBarVisibility=\"Disabled\"", panels, StringComparison.Ordinal);
        Assert.Contains("VirtualizationMode=\"Recycling\"", panels, StringComparison.Ordinal);
    }

    private static string FindSourceFile(params string[] relativePath)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine([directory.FullName, .. relativePath]);
                if (File.Exists(candidate)) return candidate;
            }
        }

        throw new FileNotFoundException("Could not find the MainWindow source.", Path.Combine(relativePath));
    }
}
