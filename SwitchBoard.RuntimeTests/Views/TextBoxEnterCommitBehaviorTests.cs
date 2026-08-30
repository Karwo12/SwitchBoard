namespace SwitchBoard.RuntimeTests.Views;

public sealed class TextBoxEnterCommitBehaviorTests
{
    [Fact]
    [Trait("Category", "Regression")]
    public void DefaultTextBoxStyle_CommitsSingleLineValuesAndPreservesSpecialEditors()
    {
        var styles = File.ReadAllText(FindSourceFile("Themes", "BaseStyles.xaml"));
        var behavior = File.ReadAllText(FindSourceFile("Controls", "TextBoxEnterCommitBehavior.cs"));
        var search = File.ReadAllText(FindSourceFile("Controls", "SearchBoxControl.xaml"));
        var rename = File.ReadAllText(FindSourceFile("Controls", "RenamableListItemControl.xaml"));

        Assert.Contains("TextBoxEnterCommitBehavior.CommitOnEnter\" Value=\"True", styles,
            StringComparison.Ordinal);
        Assert.Contains("textBox.AcceptsReturn", behavior, StringComparison.Ordinal);
        Assert.Contains("UpdateSource()", behavior, StringComparison.Ordinal);
        Assert.Contains("Keyboard.ClearFocus()", behavior, StringComparison.Ordinal);
        Assert.Contains("TextBoxEnterCommitBehavior.CommitOnEnter=\"False\"", search,
            StringComparison.Ordinal);
        Assert.Contains("TextBoxEnterCommitBehavior.CommitOnEnter=\"False\"", rename,
            StringComparison.Ordinal);
    }

    private static string FindSourceFile(params string[] relativePath)
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. relativePath]);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException("Could not find source file.", Path.Combine(relativePath));
    }
}
