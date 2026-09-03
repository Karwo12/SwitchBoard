namespace SwitchBoard.RuntimeTests.Views;

public sealed class ActivityPanelCompositionTests
{
    [Fact]
    [Trait("Category", "Regression")]
    public void ActivityViews_ShowTheSharedIconAndStatusBesideEntryNames()
    {
        var xaml = File.ReadAllText(FindSourceFile("Views", "Panels", "ActivityPanel.xaml"));

        Assert.Contains("SystemChangeStatusDotStyle", xaml, StringComparison.Ordinal);
        Assert.Contains("Source=\"{Binding IconPresentation.Icon}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{DynamicResource ActionResultStatusDotStyle}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{DynamicResource ProfileResultStatusDotStyle}\"", xaml, StringComparison.Ordinal);
        Assert.Equal(2, xaml.Split("IconPresentation.Icon", StringSplitOptions.None).Length - 1);
    }

    private static string FindSourceFile(params string[] relativePath)
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. relativePath]);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException("Could not find the Activity panel XAML.");
    }
}
