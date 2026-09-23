using System.Collections.Concurrent;
using Bevel.Pal.Abstractions;

namespace Bevel.Pal.Fake;

/// <summary>
/// Deterministic, injectable badge source (bevel-ijln). Starts empty — the scripted fake desktop
/// shows no invented unread counts — but <see cref="Set"/> lets a test (or a demo run under
/// <c>--pal=fake</c>) publish a badge for an app and watch it land on the taskbar button.
/// </summary>
public sealed class FakeAppBadgeSource : IAppBadgeSource
{
    // bundle id (or app name) → label. Concurrent because the reconcile loop reads it off-thread
    // while a test mutates it from the test thread.
    private readonly ConcurrentDictionary<string, string> _labels = new(StringComparer.Ordinal);

    public Capabilities Capabilities => FakeData.Caps;

    /// <summary>Publishes (or, with a null/empty label, clears) the badge for <paramref name="appKey"/> —
    /// a bundle id or an app display name, matching whichever key the taskbar button carries.</summary>
    public void Set(string appKey, string? label)
    {
        if (string.IsNullOrEmpty(label))
            _labels.TryRemove(appKey, out _);
        else
            _labels[appKey] = label;
    }

    /// <summary>Drops every published badge.</summary>
    public void Clear() => _labels.Clear();

    public ValueTask<IReadOnlyList<AppBadge>> GetBadgesAsync(CancellationToken ct = default)
    {
        var snapshot = _labels
            .Select(kv => new AppBadge(BundleId: kv.Key, AppName: kv.Key, Label: kv.Value))
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<AppBadge>>(snapshot);
    }
}
