using System.Text.Json.Serialization;

namespace Bevel.ComponentBus;

/// <summary>Source-generated JSON for the bus. Reflection-based JsonSerializer throws under
/// PublishAot=true, so every type crossing System.Text.Json here must be registered.</summary>
[JsonSerializable(typeof(ComponentBusEndpointFile.Endpoint))]
internal partial class BusJsonContext : JsonSerializerContext;
