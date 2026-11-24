using Bevel.Core.Vfs;
using Xunit;

namespace Bevel.Core.Tests;

public sealed class FileUriTests
{
    [Theory]
    [InlineData("/Users/test/plain.txt")]
    [InlineData("/Users/test/with space.txt")]
    [InlineData("/Users/test/50% off?.txt")]
    [InlineData("/Users/test/notes #3.md")]
    [InlineData("/Users/test/a%20b.txt")]          // a literal "%20" in the name, not an escaped space
    [InlineData("/Users/test/zażółć gęślą.txt")]
    [InlineData("/Users/test/a+b&c=d;e.txt")]
    public void A_posix_path_round_trips_through_the_uri(string path)
    {
        var uri = FileUri.FromPath(path);

        Assert.True(uri.IsFile);
        Assert.Equal(path, uri.LocalPath);
        Assert.Equal("", uri.Query);
        Assert.Equal("", uri.Fragment);
    }

    [Fact]
    public void A_question_mark_is_part_of_the_name_not_a_query()
    {
        // The regression this type exists for: UriBuilder { Path = "/a/what?.txt" } yields a URI
        // whose path stops at "what" and whose query is ".txt".
        var uri = FileUri.FromPath("/a/what?.txt");
        Assert.EndsWith("/a/what%3F.txt", uri.AbsoluteUri);
    }

    [Fact]
    public void A_windows_drive_path_keeps_its_drive_letter()
    {
        var uri = FileUri.FromPath(@"C:\Users\test\50% off.txt");
        Assert.Equal("file:///C:/Users/test/50%25%20off.txt", uri.AbsoluteUri);
    }

    [Fact]
    public void A_unc_path_carries_the_server_as_the_host()
    {
        var uri = FileUri.FromPath(@"\\server\share\dir\a b.txt");
        Assert.Equal("server", uri.Host);
        Assert.Equal("file://server/share/dir/a%20b.txt", uri.AbsoluteUri);
    }
}
