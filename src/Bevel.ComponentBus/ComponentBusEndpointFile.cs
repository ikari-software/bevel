using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bevel.ComponentBus;

/// <summary>
/// Where a component learns the bus port and nonce. This file is the REPLACEMENT for socket
/// permissions: NetMQ has no ipc:// on Windows, so the bus is loopback TCP with no filesystem gate
/// of its own. Written user-only, and on Unix with 0600.
/// </summary>
public static class ComponentBusEndpointFile
{
    /// <summary>Internal shape, public only so the source-generated context can see it.</summary>
    public sealed record Endpoint(
        [property: JsonPropertyName("port")] int Port,
        [property: JsonPropertyName("nonceBase64")] string NonceBase64);

    public static void Write(string path, int port, byte[] nonce)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(
            new Endpoint(port, Convert.ToBase64String(nonce)), BusJsonContext.Default.Endpoint);
        File.WriteAllText(path, json);

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        // On Windows the file inherits the user profile's user-only ACL — the same "0700
        // equivalent" BevelRuntimeDir documents for the existing sockets.
    }

    public static (int Port, byte[] Nonce) Read(string path)
    {
        var e = JsonSerializer.Deserialize(File.ReadAllText(path), BusJsonContext.Default.Endpoint)
                ?? throw new InvalidDataException($"empty component-bus endpoint file: {path}");
        return (e.Port, Convert.FromBase64String(e.NonceBase64));
    }
}
