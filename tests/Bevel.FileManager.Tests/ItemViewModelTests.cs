using Bevel.Core.Vfs;
using Bevel.FileManager.Components;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>bevel-so27: a differential reload hands every row a fresh node; a row whose data did not
/// change must not re-notify (and so re-measure / re-render) its display properties.</summary>
public sealed class ItemViewModelTests
{
    private static readonly DateTimeOffset When = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static VirtualNode Node(string name = "a.txt", long? size = 10, DateTimeOffset? modified = null, string ext = ".txt")
        => new()
        {
            Path = new VfsPath("test", name), DisplayName = name, Kind = VfsNodeKind.File,
            Size = size, Modified = modified ?? When, TypeDescription = "Text Document",
            IconKey = IconKey.File(ext),
        };

    private static List<string?> Raised(ItemViewModel vm)
    {
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        return raised;
    }

    [Fact]
    public void Update_with_identical_values_raises_nothing()
    {
        var vm = new ItemViewModel(Node());
        var raised = Raised(vm);

        vm.Update(Node());

        // "Nothing" visible: the only notification is Node itself, whose identity did change.
        Assert.Equal(new[] { nameof(ItemViewModel.Node) }, raised);
    }

    [Fact]
    public void Update_raises_only_the_properties_that_changed()
    {
        var vm = new ItemViewModel(Node());
        var raised = Raised(vm);

        vm.Update(Node(size: 2048));

        Assert.Contains(nameof(ItemViewModel.SizeDisplay), raised);
        Assert.Contains(nameof(ItemViewModel.Node), raised);
        Assert.DoesNotContain(nameof(ItemViewModel.DisplayName), raised);
        Assert.DoesNotContain(nameof(ItemViewModel.ModifiedDisplay), raised);
        Assert.DoesNotContain(nameof(ItemViewModel.IconKey), raised);
        Assert.DoesNotContain(nameof(ItemViewModel.TypeDescription), raised);
    }

    [Fact]
    public void Update_raises_modified_and_icon_when_they_change()
    {
        var vm = new ItemViewModel(Node());
        var raised = Raised(vm);

        vm.Update(Node(modified: When.AddHours(1), ext: ".md"));

        Assert.Contains(nameof(ItemViewModel.ModifiedDisplay), raised);
        Assert.Contains(nameof(ItemViewModel.IconKey), raised);
        Assert.DoesNotContain(nameof(ItemViewModel.SizeDisplay), raised);
    }
}
