using System.Windows.Controls;
using System.Windows.Documents;
using SwitchBoard.Controls;

namespace SwitchBoard.RuntimeTests.Controls;

[Collection("Windows runtime")]
public sealed class ListBoxDragDropTests
{
    [Fact]
    [Trait("Category", "Regression")]
    public void CanBeginFrom_InlineRun_TraversesTheLogicalTreeWithoutThrowing()
    {
        var canBegin = false;

        RunOnSta(() =>
        {
            var run = new Run("Profile name");
            var textBlock = new TextBlock();
            textBlock.Inlines.Add(run);
            var container = new ListBoxItem { Content = textBlock };

            canBegin = ListBoxDragDrop.CanBeginFrom(run, container);
        });

        Assert.True(canBegin);
    }

    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new InvalidOperationException("STA drag-and-drop scenario failed.", error);
    }
}
