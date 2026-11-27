using Avalonia.Controls;
using Avalonia.Input;
using Bevel.Core.Input;

namespace Bevel.FileManager.Components;

/// <summary>
/// Type-ahead for a <see cref="TreeView"/> (bevel-psro): flattens the VISIBLE (expanded) nodes in display
/// order, runs the shared <see cref="TypeToFind"/> matcher over their headers, and selects + scrolls to
/// the match. One helper serves both folder trees (the Filer's sidebar and the folder picker), so the
/// behaviour (prefix accumulation, single-char cycling, forward wrap) is identical to the item list.
/// Reads real typed text only — never decodes <c>Key</c> enums, so digits/accents work.
/// </summary>
internal static class TreeTypeAhead
{
    public static void Attach(TreeView tree, TypeToFind matcher)
        => tree.AddHandler(InputElement.TextInputEvent, (_, e) => OnTextInput(tree, matcher, e));

    private static void OnTextInput(TreeView tree, TypeToFind matcher, TextInputEventArgs e)
    {
        var text = e.Text;
        if (string.IsNullOrEmpty(text) || text.Any(char.IsControl)) return;

        var visible = VisibleItems(tree).ToList();
        if (visible.Count == 0) return;

        var current = tree.SelectedItem is TreeViewItem sel ? visible.IndexOf(sel) : -1;
        var idx = matcher.Match(text, visible, static i => i.Header?.ToString() ?? "", current);
        if (idx < 0) return;

        var item = visible[idx];
        tree.SelectedItem = item;
        item.BringIntoView();
        e.Handled = true;
    }

    /// <summary>All currently visible nodes (expanded branches only) in display order.</summary>
    private static System.Collections.Generic.IEnumerable<TreeViewItem> VisibleItems(TreeView tree)
    {
        foreach (var root in tree.Items.OfType<TreeViewItem>())
            foreach (var item in Walk(root))
                yield return item;
    }

    private static System.Collections.Generic.IEnumerable<TreeViewItem> Walk(TreeViewItem item)
    {
        yield return item;
        if (!item.IsExpanded) yield break;
        foreach (var child in item.Items.OfType<TreeViewItem>())
            foreach (var v in Walk(child))
                yield return v;
    }
}
