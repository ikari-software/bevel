using System;
using System.Threading;
using System.Threading.Tasks;

namespace Bevel.Core.Updates;

/// <summary>What a check found. <see cref="None"/> is both "no update" and "nothing to check against",
/// which the UI treats identically — a shell with no feed configured must look exactly like a shell that
/// is up to date, never like one that is failing.</summary>
public sealed record UpdateAvailability(bool Available, string? Version = null, string? Notes = null)
{
    public static readonly UpdateAvailability None = new(false);
}

/// <summary>
/// Where update information comes from. Deliberately an interface with no shipping implementation yet:
/// the updater is decided (UPD-01 — Velopack on all platforms, Sparkle and MSIX rejected), and wiring it
/// is bevel-ym0. Inventing a feed format here would pre-empt that decision and create a second wire
/// contract to migrate off.
///
/// What this project owns, and what is implemented, is the POLICY around the check: how often, whether
/// it survives a restart, and the rule that a check never applies anything mid-session.
/// </summary>
public interface IUpdateSource
{
    /// <summary>Asks the feed whether something newer than <paramref name="currentVersion"/> exists.
    /// Never throws: a network failure is not an error the user should see, it is simply "not now".</summary>
    ValueTask<UpdateAvailability> CheckAsync(string feedUrl, string currentVersion, CancellationToken ct);
}

/// <summary>The source used until Velopack is wired (bevel-ym0). Always reports nothing available.
///
/// It exists so the scheduler, its persistence and its settings can ship, be tested and be running
/// before the network half lands — and so "no update source" is a stated registration rather than an
/// unbound dependency that fails at resolve time.</summary>
public sealed class NoUpdateSource : IUpdateSource
{
    public static readonly NoUpdateSource Instance = new();

    public ValueTask<UpdateAvailability> CheckAsync(string feedUrl, string currentVersion, CancellationToken ct)
        => ValueTask.FromResult(UpdateAvailability.None);
}
