using System;
using Bevel.Interop;
using Xunit;

namespace Bevel.Core.Tests.Interop;

public class FinderNamingTests
{
    private static Func<string, bool> None => _ => false;
    private static Func<string, bool> Has(params string[] names) => n => Array.IndexOf(names, n) >= 0;

    [Fact]
    public void Duplicate_file_inserts_copy_before_extension()
        => Assert.Equal("report copy.pdf", FinderNaming.DuplicateName("report.pdf", isFolder: false, None));

    [Fact]
    public void Duplicate_folder_appends_copy_with_no_extension()
        => Assert.Equal("Projects copy", FinderNaming.DuplicateName("Projects", isFolder: true, None));

    [Fact]
    public void Duplicate_uses_last_extension_only()
        => Assert.Equal("archive.tar copy.gz", FinderNaming.DuplicateName("archive.tar.gz", isFolder: false, None));

    [Fact]
    public void Duplicate_dotfile_has_no_extension()
        => Assert.Equal(".bashrc copy", FinderNaming.DuplicateName(".bashrc", isFolder: false, None));

    [Fact]
    public void Duplicate_numbers_when_copy_taken()
        => Assert.Equal("x copy 2.txt", FinderNaming.DuplicateName("x.txt", isFolder: false, Has("x copy.txt")));

    [Fact]
    public void Duplicate_finds_next_free_number()
        => Assert.Equal("x copy 3.txt",
            FinderNaming.DuplicateName("x.txt", isFolder: false, Has("x copy.txt", "x copy 2.txt")));

    [Fact]
    public void Unique_returns_desired_when_free()
        => Assert.Equal("untitled folder", FinderNaming.UniqueName("untitled folder", isFolder: true, None));

    [Fact]
    public void Unique_numbers_folder_collision()
        => Assert.Equal("untitled folder 2",
            FinderNaming.UniqueName("untitled folder", isFolder: true, Has("untitled folder")));

    [Fact]
    public void Unique_numbers_file_before_extension()
        => Assert.Equal("note 2.txt", FinderNaming.UniqueName("note.txt", isFolder: false, Has("note.txt")));
}
