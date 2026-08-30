namespace SwitchBoard.RuntimeTests.Views;

public sealed class SearchBoxControlTests
{
    [Fact]
    [Trait("Category", "Regression")]
    public void SharedSearchBox_CommitsOnEnterAndClearsFocus()
    {
        var xaml = File.ReadAllText(FindSourceFile("Controls", "SearchBoxControl.xaml"));
        var code = File.ReadAllText(FindSourceFile("Controls", "SearchBoxControl.xaml.cs"));

        Assert.Contains("PreviewKeyDown=\"SearchTextBox_OnPreviewKeyDown\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Padding=\"36,7,34,7\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Margin=\"39,0,34,0\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"SearchBoxTextBoxStyle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TextBlock.TextAlignment=\"Left\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Margin=\"{TemplateBinding Padding}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("HorizontalContentAlignment = HorizontalAlignment.Left", code, StringComparison.Ordinal);
        Assert.Contains("TextAlignment = TextAlignment.Left", code, StringComparison.Ordinal);
        Assert.Contains("GetBindingExpression(TextBox.TextProperty)?.UpdateSource()", code, StringComparison.Ordinal);
        Assert.Contains("Keyboard.ClearFocus()", code, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Regression")]
    public void ActivityPanel_KeepsStyledTabResourcesAfterExtraction()
    {
        var xaml = File.ReadAllText(FindSourceFile("Views", "Panels", "ActivityPanel.xaml"));

        Assert.Contains("x:Key=\"ActivityTabButtonStyle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"ActivityTab1ButtonStyle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"ActivityTab2ButtonStyle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{DynamicResource ActivityTab1ButtonStyle}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{DynamicResource ActivityTab2ButtonStyle}\"", xaml, StringComparison.Ordinal);
    }

    private static string FindSourceFile(params string[] relativePath)
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. relativePath]);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException("Could not find the source file.", Path.Combine(relativePath));
    }
}
