using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>bevel-dw5: the stacks list editor's pure rules.</summary>
public class StackListEditorTests
{
    [Fact]
    public void Add_rejects_a_duplicate_and_normalises_a_trailing_slash()
    {
        var list = new[] { "/Users/x/Documents" };

        Assert.Same(list, StackListEditor.Add(list, "/Users/x/Documents"));       // duplicate (ordinal)
        Assert.Same(list, StackListEditor.Add(list, "/Users/x/Documents/"));      // same after normalising
        Assert.Same(list, StackListEditor.Add(list, "   "));                      // blank

        var next = StackListEditor.Add(list, "/Users/x/Downloads/");
        Assert.Equal(new[] { "/Users/x/Documents", "/Users/x/Downloads" }, next);
    }

    [Fact]
    public void Move_swaps_and_clamps_at_both_ends()
    {
        var list = new[] { "a", "b", "c" };

        Assert.Equal(new[] { "b", "a", "c" }, StackListEditor.Move(list, 0, 1));
        Assert.Equal(new[] { "a", "c", "b" }, StackListEditor.Move(list, 2, -1));

        Assert.Same(list, StackListEditor.Move(list, 0, -1));    // already at the top
        Assert.Same(list, StackListEditor.Move(list, 2, 1));     // already at the bottom
        Assert.Same(list, StackListEditor.Move(list, 9, 1));     // out of range
    }

    [Fact]
    public void Remove_out_of_range_is_a_no_op()
    {
        var list = new[] { "a", "b" };
        Assert.Same(list, StackListEditor.Remove(list, -1));
        Assert.Same(list, StackListEditor.Remove(list, 2));
        Assert.Equal(new[] { "a" }, StackListEditor.Remove(list, 1));
    }
}
