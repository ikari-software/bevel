using System;
using System.Threading;
using Avalonia.Threading;

namespace Bevel.TestSupport;

/// <summary>
/// Waiting in headless tests for work that finishes off the UI thread (the program list loading, icon
/// decodes), without a wall-clock budget that silently runs out (bevel-i145).
///
/// The loops this replaces were <c>for (i &lt; 50 &amp;&amp; !done) { RunJobs(); await Task.Delay(10); }</c>:
/// 500 ms, then carry on as if the condition held. Under a loaded full-suite run the budget ran out and the
/// test went on to render or assert against a half-loaded model, failing somewhere far from the cause, or
/// not failing and saving a wrong render. They were also <c>async</c>, and every await in a headless test
/// resumes on whatever dispatcher is current then (bevel-2nn2).
/// </summary>
public static class Settle
{
    /// <summary>Pumps the UI dispatcher until <paramref name="condition"/> holds. Throws, naming
    /// <paramref name="what"/>, if it does not within <paramref name="timeout"/> (default 10 s). The short
    /// sleep between polls yields to the thread-pool work being waited for; it is not a settle delay, and the
    /// loop returns as soon as the condition holds.</summary>
    public static void Until(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return;
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"timed out waiting for {what}");
            Thread.Sleep(5);
        }
    }
}
