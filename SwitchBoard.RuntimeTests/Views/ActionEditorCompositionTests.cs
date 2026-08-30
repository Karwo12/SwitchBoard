namespace SwitchBoard.RuntimeTests.Views;

public sealed class ActionEditorCompositionTests
{
    [Fact]
    [Trait("Category", "Regression")]
    public void AdvancedActionOptions_AreHostedByTheSpecializedEditorWithCommandsForwarded()
    {
        var host = File.ReadAllText(FindSourceFile("Controls", "ActionEditorControl.xaml"));
        var advanced = File.ReadAllText(FindSourceFile("Controls", "ActionEditors",
            "ActionAdvancedOptionsEditor.xaml"));

        Assert.Contains("<editors:ActionAdvancedOptionsEditor", host, StringComparison.Ordinal);
        Assert.Contains("CommandHost=", host, StringComparison.Ordinal);
        Assert.DoesNotContain("Advanced.ProcessParameters", host, StringComparison.Ordinal);
        Assert.Contains("Advanced.ProcessParameters", advanced, StringComparison.Ordinal);
        Assert.Contains("CommandHost.SelectAllCpusCommand", advanced, StringComparison.Ordinal);
        Assert.Contains("CommandHost.BrowseRestoreScriptCommand", advanced, StringComparison.Ordinal);
        Assert.DoesNotContain("DataContext.SelectAllCpusCommand", advanced, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Regression")]
    public void ActionEditor_TextFieldsCommitAndClearFocusOnEnter()
    {
        var host = File.ReadAllText(FindSourceFile("Controls", "ActionEditorControl.xaml"));
        var codeBehind = File.ReadAllText(FindSourceFile("Controls", "ActionEditorControl.xaml.cs"));

        Assert.Contains("PreviewKeyDown=\"ActionEditor_OnPreviewKeyDown\"", host, StringComparison.Ordinal);
        Assert.Contains("textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();", codeBehind,
            StringComparison.Ordinal);
        Assert.Contains("Keyboard.ClearFocus();", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void ActionEditor_ResetsItsScrollAfterChangingSelectedProfile()
    {
        var codeBehind = File.ReadAllText(FindSourceFile("Controls", "ActionEditorControl.xaml.cs"));

        Assert.Contains("nameof(MainWindowViewModel.SelectedProfile)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("DispatcherPriority.ContextIdle", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ScrollToTop()", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Delay", codeBehind, StringComparison.Ordinal);
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

        throw new FileNotFoundException("Could not find an action editor source.", Path.Combine(relativePath));
    }
}
