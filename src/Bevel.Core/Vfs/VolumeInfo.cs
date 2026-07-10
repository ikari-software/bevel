namespace Bevel.Core.Vfs;

/// <summary>
/// Typed metadata for <see cref="VfsNodeKind.Volume"/> nodes (bevel-972) — replaces the former
/// stringly-typed ExtraColumns bag ("MountPath"/"FreeSpace"/… keys consumed via casts).
/// Surfaced by the My Computer details view and the volume Properties sheet.
/// </summary>
/// <param name="MountPath">Native root the volume stands for (e.g. "/", "C:\").</param>
/// <param name="TotalSize">Total capacity in bytes.</param>
/// <param name="FreeSpace">Free bytes.</param>
/// <param name="Format">Filesystem format (e.g. "apfs", "NTFS").</param>
public sealed record VolumeInfo(string MountPath, long TotalSize, long FreeSpace, string Format);
