namespace Bevel.Pal.MacOS;

/// <summary>
/// Time source for <see cref="HelperLifecycle"/>'s monitor loop (bevel-vgg / review AD7).
/// Injected so tests drive the loop deterministically with a manual clock instead of real
/// delays; production uses <see cref="SystemClock"/>.
/// </summary>
internal interface IClock
{
    /// <summary>Completes after <paramref name="delay"/>, or throws <see cref="OperationCanceledException"/>.</summary>
    Task Delay(TimeSpan delay, CancellationToken ct);
}

internal sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    private SystemClock() { }

    public Task Delay(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);
}
