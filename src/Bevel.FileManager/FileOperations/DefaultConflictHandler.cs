using Bevel.Core.Vfs;

namespace Bevel.FileManager.FileOperations;

/// <summary>
/// Safe default conflict handler used until the interactive Win2000 "Confirm File Replace"
/// dialog lands (M1). It cancels the operation on the first conflict rather than silently
/// overwriting or skipping, so no data is lost without the user's involvement.
/// </summary>
public sealed class DefaultConflictHandler : IConflictHandler
{
    public ConflictResolution ResolveConflict(
        VfsPath source, VfsPath destination, long? sourceSize, long? destSize,
        DateTimeOffset? sourceModified, DateTimeOffset? destModified, ConflictScope scope)
        => ConflictResolution.Cancel;
}
