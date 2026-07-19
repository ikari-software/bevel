namespace Bevel.Interop.AppleEvents;

// ── Parsed AppleScript object specifier (08-os-interop.md §2.1.2) ─────────────────────────────
//
// The native NSAppleEventManager handler flattens an inbound AEDesc object-specifier chain into
// this managed AST (that flattening is the only native part); AppleEventObjectResolver then resolves
// it against the command model / VFS, entirely in managed code. Keeping the AST separate is what
// makes the resolver — "a from-scratch AE object-model resolver" — unit-testable with no AE runtime.

/// <summary>The Finder-mirrored element classes the resolver understands (subset, §2.1.2).</summary>
public enum AeClass { Item, File, Folder, Disk }

/// <summary>The <c>whose</c> keys v1 supports (full filter algebra deferred, §2.1.2).</summary>
public enum WhoseKey { Name, NameExtension, Kind }

/// <summary>Comparison operators for a <c>whose</c> test.</summary>
public enum WhoseOp { Equals, BeginsWith, EndsWith, Contains }

/// <summary>A single <c>whose</c> predicate, e.g. <c>whose name extension is "txt"</c>.</summary>
public sealed record WhoseFilter(WhoseKey Key, WhoseOp Op, string Value);

/// <summary>Base of the object-specifier AST.</summary>
public abstract record ObjectSpecifier;

/// <summary>An application property that names a base container: <c>home</c>, <c>desktop</c>,
/// <c>trash</c>, <c>startup disk</c>.</summary>
public sealed record PropertySpecifier(string Property) : ObjectSpecifier;

/// <summary>A literal POSIX path: <c>POSIX file "/Users/x"</c>.</summary>
public sealed record PosixPathSpecifier(string Path) : ObjectSpecifier;

/// <summary>An element addressed by name within a container: <c>folder "Documents" of home</c>.
/// A null <see cref="Container"/> defaults to <c>home</c>.</summary>
public sealed record ElementByName(AeClass Class, string Name, ObjectSpecifier? Container) : ObjectSpecifier;

/// <summary>An element addressed by 1-based index (<c>item 1 of …</c>); <c>-1</c> = last.</summary>
public sealed record ElementByIndex(AeClass Class, int Index, ObjectSpecifier? Container) : ObjectSpecifier;

/// <summary><c>every &lt;class&gt; of &lt;container&gt;</c>, optionally filtered by a <c>whose</c> test.</summary>
public sealed record EveryElement(AeClass Class, ObjectSpecifier? Container, WhoseFilter? Filter) : ObjectSpecifier;
