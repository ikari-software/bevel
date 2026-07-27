using Bevel.Core.Vfs;
using Bevel.FileManager.Components;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>Folder Options "Hide extensions for known types": ItemViewModel.DisplayName strips a file's
/// extension when the app-wide flag is on — but never a folder, a dotfile, or an extensionless name, and
/// only the LAST extension. RealName (used for rename) always stays the full on-disk name.</summary>
public class ExtensionHidingTest
{
    private static ItemViewModel Vm(string name, VfsNodeKind kind = VfsNodeKind.File)
        => new(new StubNode(name, kind));

    [Fact]
    public void Strips_only_when_flag_on_and_only_for_files()
    {
        try
        {
            ItemViewModel.HideKnownExtensions = false;
            Assert.Equal("report.txt", Vm("report.txt").DisplayName);   // off → unchanged

            ItemViewModel.HideKnownExtensions = true;
            Assert.Equal("report", Vm("report.txt").DisplayName);        // file → stripped
            Assert.Equal("report", Vm("report").DisplayName);            // extensionless → unchanged
            Assert.Equal(".bashrc", Vm(".bashrc").DisplayName);          // dotfile → unchanged
            Assert.Equal("archive.tar", Vm("archive.tar.gz").DisplayName); // only last extension
            Assert.Equal("Photos.d", Vm("Photos.d", VfsNodeKind.Folder).DisplayName); // folder → unchanged
            Assert.Equal("report.txt", Vm("report.txt").RealName);      // rename uses the real name
        }
        finally { ItemViewModel.HideKnownExtensions = false; }
    }

    private sealed class StubNode : IVfsNode
    {
        public StubNode(string name, VfsNodeKind kind) { DisplayName = name; Kind = kind; }
        public VfsPath Path => new("file", "/" + DisplayName);
        public string DisplayName { get; }
        public VfsNodeKind Kind { get; }
        public bool MightHaveChildren => Kind == VfsNodeKind.Folder;
        public long? Size => null;
        public System.DateTimeOffset? Modified => null;
        public string TypeDescription => "";
        public IconKey IconKey => default;
        public VfsCapabilities Caps => VfsCapabilities.None;
    }
}
