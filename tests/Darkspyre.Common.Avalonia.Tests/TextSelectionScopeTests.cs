using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Darkspyre.Agent.Common.UI.Controls;
using Darkspyre.Agent.Common.UI.Markdown;
using LiveMarkdown.Avalonia;
using Xunit;

namespace Darkspyre.Common.Avalonia.Tests;

public sealed class TextSelectionScopeTests
{
    /// <summary>
    /// A drag from a heading to a note selects both and the label between them, but not a button's label, and the
    /// copied text joins them with line breaks.
    /// </summary>
    [Fact]
    public Task ADragJoinsLabelsAndParagraphsButNotButtons() => ViewSession.RunAsync(async () =>
    {
        TextBlock heading = Forms.Heading("Heading"), label = Forms.Text("A label"), note = PanelParts.Note("The note");
        TextSelectionScope scope = new() { Child = new StackPanel { Children = { heading, label, new Button { Content = "Press" }, note } } };
        Window window = ViewSession.Show(scope);
        CopyHandlerFixture.Attach(window);

        Input.Drag(window, Input.At(heading, window, 1), Input.At(note, window, note.Bounds.Width - 1));

        Assert.Equal("Heading\nA label\nThe note", scope.SelectedText);
        Input.Press(window, Input.Keys.Copy[0]);
        await ViewSession.WaitForAsync(() => CopyHandlerFixture.ClipboardAsync(window).Result == "Heading\nA label\nThe note");
    });

    /// <summary>
    /// With a text box focused, a drag from a prompt to a second reply selects across both replies and the expanded tool
    /// row, skips the collapsed one, and moves focus to the scope, so the Copy key copies the selection with a blank line
    /// between blocks. A press on the collapsed row's title opens it and starts no selection.
    /// </summary>
    [Fact]
    public Task ADragCrossesRepliesSkipsCollapsedRowsAndIncludesExpandedOnes() => ViewSession.RunAsync(async () =>
    {
        TextBox composer = new() { Text = "Composer text" };
        TextBlock prompt = new() { Text = "Run the tests." };
        MarkdownViewer first = new() { Text = "I will run them." }, second = new() { Text = "All tests pass." };
        (Expander closed, TextBlock closedTitle) = ToolRow("Read the design", "Design text", open: false);
        (Expander open, _) = ToolRow("Ran the tests", "26 passed", open: true);
        StackPanel transcript = new() { Spacing = 8, Children = { Block(prompt), Block(first), Block(closed), Block(open), Block(second) } };
        TextSelectionScope scope = new() { Child = transcript };
        Window window = ViewSession.Show(new DockPanel { Children = { composer, scope } });
        DockPanel.SetDock(composer, Dock.Top);
        CopyHandlerFixture.Attach(window);
        await ViewSession.WaitForAsync(() => Markdown(second).Any());
        composer.Focus();
        composer.SelectAll();

        MarkdownTextBlock end = Markdown(second).Last();
        Input.Drag(window, Input.At(prompt, window, 1), Input.At(end, window, end.Bounds.Width - 1));

        const string Expected = "Run the tests.\n\nI will run them.\n\nRan the tests\n26 passed\n\nAll tests pass.";
        Assert.Equal(Expected, scope.SelectedText);
        Assert.Same(scope, window.FocusManager!.GetFocusedElement());
        Input.Press(window, Input.Keys.Copy[0]);
        await ViewSession.WaitForAsync(() => CopyHandlerFixture.ClipboardAsync(window).Result == Expected);

        Input.Click(window, Input.At(closedTitle, window, 2));
        Assert.True(closed.IsExpanded);
        Assert.Equal(Expected, scope.SelectedText);
    });

    /// <summary>
    /// After a click that selects nothing, the Select All key and the menu's Select All select every joining text of
    /// that scope and skip collapsed content; a press in a second scope of the window clears the first.
    /// </summary>
    [Fact]
    public Task SelectAllSelectsEveryJoiningTextAndOneSelectionPerWindow() => ViewSession.RunAsync(() =>
    {
        TextBlock one = new() { Text = "One" }, two = new() { Text = "Two" }, other = new() { Text = "Other surface" };
        Expander collapsed = new() { Header = "Header", Content = new TextBlock { Text = "Hidden" }, IsExpanded = false };
        TextSelectionScope scope = new() { Child = new StackPanel { Children = { one, collapsed, two } } };
        TextSelectionScope second = new() { Child = other };
        Window window = ViewSession.Show(new StackPanel { Children = { scope, second } });

        Input.Click(window, Input.At(one, window, 2));
        Assert.False(scope.CanCopy);
        Input.Press(window, Input.Keys.SelectAll[0]);
        Assert.Equal("One\nTwo", scope.SelectedText);

        scope.ClearSelection();
        window.MouseDown(Input.At(two, window, 2), global::Avalonia.Input.MouseButton.Right);
        window.MouseUp(Input.At(two, window, 2), global::Avalonia.Input.MouseButton.Right);
        ViewSession.Settle();
        Assert.True(SelectionMenu.Text.IsOpen);
        SelectionMenu.Text.Items.OfType<MenuItem>().Single(item => item.Header is "Select All").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        SelectionMenu.Text.Hide();
        Assert.Equal("One\nTwo", scope.SelectedText);

        Input.Drag(window, Input.At(other, window, 1), Input.At(other, window, 30));
        Assert.True(second.CanCopy);
        Assert.False(scope.CanCopy);
        Assert.Same(second, TextSelection.CurrentScope(window));
        return Task.CompletedTask;
    });

    /// <summary>Select All then Copy on a reply gives its text as shown: no heading marks, bold marks or fences.</summary>
    [Fact]
    public Task CopyGivesTheTextAsShown() => ViewSession.RunAsync(async () =>
    {
        MarkdownViewer viewer = new() { Text = "# Result\n\nIt is **done**.\n\n```\nlet x = 1;\n```" };
        TextSelectionScope scope = new() { Child = viewer };
        Window window = ViewSession.Show(scope);
        CopyHandlerFixture.Attach(window);
        await ViewSession.WaitForAsync(() => Markdown(viewer).Count() >= 3);

        scope.SelectAll();
        scope.Copy();

        await ViewSession.WaitForAsync(() => CopyHandlerFixture.ClipboardAsync(window).Result is not null);
        Assert.Equal("Result\nIt is done.\nlet x = 1;", await CopyHandlerFixture.ClipboardAsync(window));
    });

    /// <summary>A drag inside one row: the scope says its selection lies in that row and not in the other.</summary>
    [Fact]
    public Task HasSelectionInNamesTheRowTheSelectionLiesIn() => ViewSession.RunAsync(() =>
    {
        TextBlock first = new() { Text = "First row text" }, second = new() { Text = "Second row text" };
        StackPanel firstRow = new() { Children = { first } }, secondRow = new() { Children = { second } };
        TextSelectionScope scope = new() { Child = new StackPanel { Children = { firstRow, secondRow } } };
        Window window = ViewSession.Show(scope);
        Assert.False(scope.HasSelectionIn(secondRow));

        Input.Drag(window, Input.At(second, window, 1), Input.At(second, window, 40));

        Assert.True(scope.HasSelectionIn(secondRow));
        Assert.True(scope.HasSelectionIn(second));
        Assert.False(scope.HasSelectionIn(firstRow));
        return Task.CompletedTask;
    });

    private static Border Block(Control content)
    {
        Border block = new() { Child = content };
        TextSelection.SetIsBlock(block, true);
        return block;
    }

    // A tool row: its title joins only while the row is open.
    private static (Expander Row, TextBlock Title) ToolRow(string title, string output, bool open)
    {
        TextBlock header = new() { Text = title };
        Expander row = new() { Header = header, Content = new TextBlock { Text = output }, IsExpanded = open };
        void Follow() => TextSelection.SetMode(header, row.IsExpanded ? TextSelectionMode.Include : TextSelectionMode.Skip);
        row.PropertyChanged += (_, change) =>
        {
            if (change.Property == Expander.IsExpandedProperty) Follow();
        };
        Follow();
        return (row, header);
    }

    private static IEnumerable<MarkdownTextBlock> Markdown(Visual root) => root.GetVisualDescendants().OfType<MarkdownTextBlock>()
        .Where(block => !string.IsNullOrEmpty(block.Inlines?.Text ?? block.Text));
}
