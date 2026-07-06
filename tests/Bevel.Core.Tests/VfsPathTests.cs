using Bevel.Core.Vfs;
using Xunit;

namespace Bevel.Core.Tests;

public class VfsPathTests
{
    [Fact]
    public void Root_has_empty_value_and_is_root()
    {
        var root = VfsPath.Root("file");
        Assert.Equal("file", root.Scheme);
        Assert.Equal("", root.Value);
        Assert.True(root.IsRoot);
    }

    [Theory]
    [InlineData("a/b/", "a/b")]      // trailing slash stripped
    [InlineData("a\\b", "a/b")]      // backslashes canonicalized
    [InlineData("a\\b\\", "a/b")]    // backslash + trailing
    [InlineData("/Users/x/", "/Users/x")]
    [InlineData("/", "/")]           // lone POSIX root preserved
    [InlineData("", "")]
    public void Constructor_normalizes_value(string input, string expected)
    {
        Assert.Equal(expected, new VfsPath("file", input).Value);
    }

    [Fact]
    public void Constructor_rejects_null_scheme()
    {
        Assert.Throws<ArgumentNullException>(() => new VfsPath(null!, "x"));
    }

    [Fact]
    public void Combine_appends_segment_under_a_folder()
    {
        var parent = new VfsPath("file", "/Users/x");
        Assert.Equal("/Users/x/docs", VfsPath.Combine(parent, "docs").Value);
    }

    [Fact]
    public void Combine_onto_root_drops_leading_separator()
    {
        var root = VfsPath.Root("file");
        Assert.Equal("Users", VfsPath.Combine(root, "Users").Value);
    }

    [Theory]
    [InlineData("/a/b/", "docs/")]   // trailing slashes on both sides
    [InlineData("/a/b", "/docs")]    // leading slash on child
    [InlineData("/a/b", "\\docs\\")] // backslashes on child
    public void Combine_trims_separators_around_the_child(string parent, string child)
    {
        Assert.Equal("/a/b/docs", VfsPath.Combine(new VfsPath("file", parent), child).Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("\\")]
    public void Combine_with_empty_child_returns_parent_unchanged(string child)
    {
        var parent = new VfsPath("file", "/a/b");
        Assert.Equal(parent, VfsPath.Combine(parent, child));
    }

    [Theory]
    [InlineData("/Users/x/docs", "/Users/x")]
    [InlineData("a/b/c", "a/b")]
    [InlineData("top", "")]           // top-level entry has empty parent value
    [InlineData("/Users", "")]        // parent of a first-level absolute entry is root
    public void ParentValue_returns_everything_before_the_last_separator(string value, string expected)
    {
        Assert.Equal(expected, new VfsPath("file", value).ParentValue);
    }

    [Theory]
    [InlineData("/Users/x/file.txt", "file.txt")]
    [InlineData("a/b/c", "c")]
    [InlineData("solo", "solo")]
    public void FileName_returns_the_last_segment(string value, string expected)
    {
        Assert.Equal(expected, new VfsPath("file", value).FileName);
    }

    [Fact]
    public void Parent_returns_a_path_in_the_same_scheme()
    {
        var p = new VfsPath("computer", "Macintosh HD/Users");
        var parent = p.Parent;
        Assert.Equal("computer", parent.Scheme);
        Assert.Equal("Macintosh HD", parent.Value);
    }

    [Fact]
    public void ToString_uses_the_canonical_vfs_form()
    {
        Assert.Equal("vfs://file//Users/x", new VfsPath("file", "/Users/x").ToString());
        Assert.Equal("vfs://computer/", VfsPath.Root("computer").ToString());
    }

    [Theory]
    [InlineData("vfs://file//Users/x", "file", "/Users/x")]
    [InlineData("vfs://computer/Macintosh HD", "computer", "Macintosh HD")]
    [InlineData("vfs://computer/", "computer", "")]
    [InlineData("vfs://computer", "computer", "")]
    public void Parse_reads_the_canonical_form(string s, string scheme, string value)
    {
        var p = VfsPath.Parse(s);
        Assert.Equal(scheme, p.Scheme);
        Assert.Equal(value, p.Value);
    }

    [Theory]
    [InlineData("/Users/x")]                 // no scheme prefix
    [InlineData("http://example.com/x")]     // wrong prefix
    [InlineData("")]
    [InlineData("vfs://")]                    // no scheme
    [InlineData("vfs:///value")]              // empty scheme
    public void TryParse_rejects_non_canonical_strings(string s)
    {
        Assert.False(VfsPath.TryParse(s, out _));
    }

    [Fact]
    public void Parse_throws_on_invalid_input()
    {
        Assert.Throws<FormatException>(() => VfsPath.Parse("/not/a/vfs/path"));
    }

    [Theory]
    [InlineData("file", "/Users/x/My Documents")]  // spaces
    [InlineData("file", "/Users/x/a+b&c=d")]        // characters a Uri parser would mangle
    [InlineData("computer", "")]
    [InlineData("zip", "archive.zip/inner/leaf.txt")]
    public void ToString_and_Parse_round_trip_exactly(string scheme, string value)
    {
        var original = new VfsPath(scheme, value);
        var reparsed = VfsPath.Parse(original.ToString());
        Assert.Equal(original, reparsed);
    }

    [Fact]
    public void Equality_ignores_trailing_separator_differences()
    {
        Assert.Equal(new VfsPath("file", "/a/b"), new VfsPath("file", "/a/b/"));
        Assert.Equal(new VfsPath("file", "/a/b"), new VfsPath("file", "\\a\\b"));
    }
}
