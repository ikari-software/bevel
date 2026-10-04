using NetMQ;
using Xunit;

namespace Bevel.ComponentBus.Tests;

/// <summary>
/// NetMQ keeps a process-wide context with its own threads. Without this the test host can hang at
/// exit. Cleanup happens ONCE for the whole assembly — never between tests, which would tear down
/// the shared context other tests still need.
/// </summary>
public sealed class NetMqAssemblyFixture : IDisposable
{
    public void Dispose() => NetMQConfig.Cleanup(block: false);
}

[CollectionDefinition("NetMQ")]
public sealed class NetMqCollection : ICollectionFixture<NetMqAssemblyFixture> { }
