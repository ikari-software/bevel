using NetMQ;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// NetMQ keeps a process-wide context with its own threads. Without this the test host can hang at
/// exit once a test in this assembly creates a <c>ComponentBusServer</c>. Cleanup happens ONCE for
/// the whole assembly — never between tests, which would tear down the shared context other tests
/// still need. Mirrors <c>tests/Bevel.ComponentBus.Tests/AssemblyFixture.cs</c>.
/// </summary>
public sealed class NetMqAssemblyFixture : IDisposable
{
    public void Dispose() => NetMQConfig.Cleanup(block: false);
}

[CollectionDefinition("NetMQ")]
public sealed class NetMqCollection : ICollectionFixture<NetMqAssemblyFixture> { }
