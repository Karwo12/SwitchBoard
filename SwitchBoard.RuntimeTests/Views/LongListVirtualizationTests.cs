namespace SwitchBoard.RuntimeTests.Views;

public sealed class LongListVirtualizationTests
{
    [Fact]
    [Trait("Category", "Regression")]
    public void ProfileAndActionLists_EnableRecyclingWithoutAnOuterLogicalScrollViewer()
    {
        var mainWindow = File.ReadAllText(FindSourceFile("Views", "MainWindow.xaml"));
        var actionEditor = File.ReadAllText(FindSourceFile("Controls", "ActionEditorControl.xaml"));

        var rootListStart = mainWindow.IndexOf("ItemsSource=\"{Binding FilteredRootNavigationItems}\"",
            StringComparison.Ordinal);
        var rootListEnd = mainWindow.IndexOf("</ListBox>", rootListStart, StringComparison.Ordinal);
        var rootList = mainWindow[rootListStart..rootListEnd];
        Assert.Contains("ScrollViewer.CanContentScroll=\"True\"", rootList, StringComparison.Ordinal);
        Assert.Contains("VirtualizingPanel.VirtualizationMode=\"Recycling\"", rootList, StringComparison.Ordinal);

        var actionListStart = actionEditor.IndexOf("x:Name=\"ActionList\"", StringComparison.Ordinal);
        var actionTemplateStart = actionEditor.IndexOf("<ListBox.ItemTemplate>", actionListStart,
            StringComparison.Ordinal);
        var actionList = actionEditor[actionListStart..actionTemplateStart];
        Assert.Contains("ScrollViewer.CanContentScroll=\"True\"", actionList, StringComparison.Ordinal);
        Assert.Contains("VirtualizingPanel.VirtualizationMode=\"Recycling\"", actionList,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ScrollViewer.CanContentScroll=\"False\"", actionList,
            StringComparison.Ordinal);
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

        throw new FileNotFoundException("Could not find a source file.", Path.Combine(relativePath));
    }
}
