namespace SwitchBoard.RuntimeTests.Views;

public sealed class MainWindowFocusCommitTests
{
    [Fact]
    [Trait("Category", "Regression")]
    public void MainWindow_CommitsAndClearsTextFocusWhenClickingOutsideAnEditableInput()
    {
        var source = File.ReadAllText(FindSourceFile("Views", "MainWindow.xaml.cs"));

        Assert.Contains("InputManager.Current.PreProcessInput += MainWindowOnPreProcessInput", source,
            StringComparison.Ordinal);
        Assert.Contains("MainWindowOnPreProcessInput", source, StringComparison.Ordinal);
        Assert.Contains("Mouse.PreviewMouseDownEvent", source, StringComparison.Ordinal);
        Assert.Contains("IsPartOfThisWindow(source)", source, StringComparison.Ordinal);
        Assert.Contains("IsInsideEditableInput(source)", source, StringComparison.Ordinal);
        Assert.Contains("GetBindingExpression(TextBox.TextProperty)?.UpdateSource()", source, StringComparison.Ordinal);
        Assert.Contains("Keyboard.ClearFocus()", source, StringComparison.Ordinal);
        Assert.Contains("current is TextBoxBase or PasswordBox", source, StringComparison.Ordinal);
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
