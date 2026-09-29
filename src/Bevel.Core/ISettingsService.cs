namespace Bevel.Core;

/// <summary>
/// The settings-store contract every UI surface talks to (bevel-6nve). Extracted from the concrete
/// <see cref="SettingsService"/> so a role that must NOT open the SQLite DB directly (Filer / Desktop
/// peers) can be handed a remote, IPC-backed implementation while the DB-owning core keeps the real one —
/// without either caller knowing which it holds.
///
/// <para>Stage A is a pure refactor: <see cref="SettingsService"/> is still the ONLY implementation and every
/// role is wired to it. The two extra wire helpers (<see cref="SnapshotJson"/> /
/// <see cref="ApplyPatchJsonAsync"/>) are the seam the later core-owned IPC path serializes over: the core
/// snapshots its state to the wire and applies a changed-keys merge patch as the sole writer.</para>
/// </summary>
public interface ISettingsService : IDisposable
{
    /// <summary>Current settings snapshot.</summary>
    BevelSettings Current { get; }

    /// <summary>Last-loaded change version (monotonic; bumped on every write). 0 until first load.</summary>
    int Version { get; }

    /// <summary>Raised after an external write is pulled in (poll reload, or the later core broadcast).</summary>
    event Action? Changed;

    /// <summary>Whitelisted overrides for <paramref name="themeId"/>, created empty on first access.</summary>
    ThemeOverrides ThemeOverridesFor(string themeId);

    /// <summary>Load settings from the backing store, merging with defaults.</summary>
    Task LoadAsync(CancellationToken ct = default);

    /// <summary>Write the current settings (whole-blob, last-writer-wins).</summary>
    Task SaveAsync(CancellationToken ct = default);

    /// <summary>Apply a delta to the typed model and persist, merging with any concurrent peer edit.</summary>
    Task UpdateAsync(Action<BevelSettings> update, CancellationToken ct = default);

    /// <summary>Apply a delta to a theme's whitelisted overrides and persist, merging concurrent edits.</summary>
    Task UpdateThemeOverridesAsync(string themeId, Action<ThemeOverrides> update, CancellationToken ct = default);

    /// <summary>Pick up an external write if the store's version moved past our last-loaded one; raises
    /// <see cref="Changed"/> and returns true when it did. A remote implementation is a no-op returning false.</summary>
    Task<bool> ReloadIfChangedAsync(CancellationToken ct = default);

    /// <summary>Serialize the current settings to the canonical persisted JSON blob — byte-identical to
    /// what a <see cref="LoadAsync"/> round-trips. The core pushes this as its snapshot on the wire.</summary>
    string SnapshotJson();

    /// <summary>Merge the changed top-level keys carried in <paramref name="patchJson"/> onto the current
    /// state, then migrate + project + persist. The core (sole writer) applies UI-role update requests
    /// through this path.</summary>
    Task ApplyPatchJsonAsync(string patchJson, CancellationToken ct = default);
}
