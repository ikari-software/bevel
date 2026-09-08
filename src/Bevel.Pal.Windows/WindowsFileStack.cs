using Bevel.Pal.Abstractions;

namespace Bevel.Pal.Windows;

/// <summary>U8 (bevel-ncfp.8) lands IFileOperation (STA COM) for copy/move/recycle/rename. Bootstrap stub.</summary>
public sealed class WindowsFileOperations : IFileOperations
{
    public Capabilities Capabilities => NotYet.Unavailable;

    public Task CopyAsync(IReadOnlyList<string> sources, string destDir, CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);
    public Task MoveAsync(IReadOnlyList<string> sources, string destDir, CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);
    public Task DeleteAsync(IReadOnlyList<string> paths, DeleteMode mode, CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);
    public Task RenameAsync(string path, string newName, CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);
}

/// <summary>U8: ShellExecuteEx open/verbs + SHAssocEnumHandlers "Open With". Bootstrap stub throws on
/// open, no-ops preview (Windows has no Quick Look analogue).</summary>
public sealed class WindowsFileOpener : IFileOpener
{
    public Task OpenPathAsync(string path, CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);
    public Task PreviewAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>U8: SHGetFileInfo/IExtractIcon on the STA thread → BGRA into the shared pool. Bootstrap stub
/// returns a blank 1x1 so the pooled-icon pipeline and any bound Image stay non-null.</summary>
public sealed class WindowsIconProvider : IIconProvider
{
    private static readonly PalImage Blank = new(1, 1, new byte[4]);

    public ValueTask<PalImage> GetIconAsync(string pathOrExtension, int size, CancellationToken ct = default)
        => ValueTask.FromResult(Blank);

    public event EventHandler? IconInvalidated;
}

/// <summary>.NET reports a Windows DriveInfo.VolumeLabel natively, so this stays a null-returning
/// pass-through (callers fall back to the mount path) unless a richer source is wanted.</summary>
public sealed class WindowsVolumeLabelSource : IVolumeLabelSource
{
    public string? LabelFor(string mountPath) => null;
}
