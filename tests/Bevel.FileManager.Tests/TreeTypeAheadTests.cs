using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Bevel.Core.Input;
using Bevel.FileManager.Components;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>
/// bevel-psro: <see cref="TreeTypeAhead"/> wires the shared <see cref="TypeToFind"/> matcher to a folder
/// tree — typing selects the next visible node with the prefix, repeating the letter cycles, collapsed
/// children are skipped, and digits/accents work (real text input, no Key decode).
/// </summary>
public class TreeTypeAheadTests
{
    private long _nowMs;

    private TreeView BuildTree(out Window host)
    {
        var apple = new TreeViewItem { Header = "Apple", IsExpanded = true };
        apple.Items.Add(new TreeViewItem { Header = "Apricot" });
        apple.Items.Add(new TreeViewItem { Header = "Avocado" });
        var cherry = new TreeViewItem { Header = "Cherry", IsExpanded = false };
        cherry.Items.Add(new TreeViewItem { Header = "Cranberry" });   // collapsed → invisible
        var tree = new TreeView();
        tree.Items.Add(apple);
        tree.Items.Add(new TreeViewItem { Header = "Banana" });
        tree.Items.Add(cherry);
        tree.Items.Add(new TreeViewItem { Header = "Étude" });

        var matcher = new TypeToFind(() => _nowMs);
        TreeTypeAhead.Attach(tree, matcher);

        host = new Window { SystemDecorations = SystemDecorations.None, Content = tree, Width = 300, Height = 300 };
        host.Show();
        Dispatcher.UIThread.RunJobs();
        return tree;
    }

    private static void Type(TreeView tree, string text)
        => tree.RaiseEvent(new TextInputEventArgs
        {
            RoutedEvent = InputElement.TextInputEvent,
            Text = text,
            Source = tree,
        });

    private static string Selected(TreeView tree) => (tree.SelectedItem as TreeViewItem)?.Header?.ToString() ?? "";

    [AvaloniaFact]
    public void Typing_a_letter_selects_the_next_visible_node_with_that_prefix()
    {
        var tree = BuildTree(out var host);
        try
        {
            _nowMs = 0;
            Type(tree, "b");
            Assert.Equal("Banana", Selected(tree));

            // Past the 700 ms reset window the prefix starts fresh: "a" from Banana wraps forward to Apple.
            _nowMs = 1000;
            Type(tree, "a");
            Assert.Equal("Apple", Selected(tree));
        }
        finally { host.Close(); }
    }

    [AvaloniaFact]
    public void Repeating_the_letter_cycles_through_the_matches()
    {
        var tree = BuildTree(out var host);
        try
        {
            _nowMs = 0; Type(tree, "a");
            Assert.Equal("Apple", Selected(tree));
            _nowMs = 10; Type(tree, "a");
            Assert.Equal("Apricot", Selected(tree));
            _nowMs = 20; Type(tree, "a");
            Assert.Equal("Avocado", Selected(tree));
            _nowMs = 30; Type(tree, "a");   // wraps around once → back to Apple
            Assert.Equal("Apple", Selected(tree));
        }
        finally { host.Close(); }
    }

    [AvaloniaFact]
    public void Collapsed_children_are_not_matched()
    {
        var tree = BuildTree(out var host);
        try
        {
            _nowMs = 0; Type(tree, "c");
            Assert.Equal("Cherry", Selected(tree));       // never Cranberry (collapsed)
            _nowMs = 10; Type(tree, "c");
            Assert.Equal("Cherry", Selected(tree));       // only one visible c-node → stays
        }
        finally { host.Close(); }
    }

    [AvaloniaFact]
    public void Digits_and_accented_characters_match()
    {
        var tree = BuildTree(out var host);
        try
        {
            _nowMs = 0; Type(tree, "é");
            Assert.Equal("Étude", Selected(tree));
        }
        finally { host.Close(); }
    }
}
