using System.Threading.Tasks;
using Bevel.App.ShellCore;
using Xunit;

namespace Bevel.Taskbar.Tests;

public class ShellCoreClientTests
{
    [Fact]
    public async Task DisposeAsync_is_idempotent()
    {
        // Regression: DisposeAsync ran twice on shutdown (the DI container AND the host-teardown path).
        // The second pass called CancelAsync() on the already-disposed _lifetime CancellationTokenSource,
        // throwing ObjectDisposedException that went UNHANDLED during shutdown and crashed the shell on
        // restart / quit — it "just disappeared and never came back". Disposing twice must be a no-op.
        var client = new ShellCoreClient("/tmp/bevel-nonexistent-test.sock", new byte[32], "shellcore");
        await client.DisposeAsync();
        await client.DisposeAsync(); // must not throw
    }
}
