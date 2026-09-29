using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Bevel.App.ShellCore;

/// <summary>
/// Discovery + shared-secret resolution for the taskbar↔Filer automation channel (bevel-uldj).
///
/// <para>The split shell hosts each Filer window as its own <c>--role=filer</c> process, so the
/// window-coupled automation verbs (<c>select</c>, <c>query windows</c>, <c>query selection</c>) that
/// arrive on the persistent taskbar's bevelctl/bevel:// socket have no in-process window graph to
/// answer them. This channel lets the taskbar reach the live Filers.</para>
///
/// <para><b>Registration model.</b> Each Filer REGISTERS by binding a control socket
/// <c>filer-&lt;pid&gt;.sock</c> in a shared rendezvous directory and answering the
/// <see cref="FilerRequest"/> protocol against its own in-process
/// <see cref="Bevel.Interop.IShellSurface"/>. The taskbar DISCOVERS live Filers by scanning that
/// directory and dialling each socket as a short-lived client — the natural request/response
/// direction (caller = taskbar, target = one Filer), so the existing
/// <see cref="Bevel.ShellCore.Ipc"/> transport is reused verbatim in both directions with no
/// transport surgery. An Filer DEREGISTERS by unlinking its socket on clean exit; a CRASHED
/// Filer leaves a stale socket that the taskbar prunes on the first connect-refused dial (its next
/// pid gets a fresh filename, so lazy pruning suffices).</para>
///
/// <para><b>Security.</b> The rendezvous dir is a taskbar-minted, env-discoverable
/// (<c>BEVEL_FILER_DIR</c>) location hardened to 0700; the shared HMAC nonce is minted once and
/// published via env (<c>BEVEL_FILER_TOKEN</c>, inherited by spawned Filers) with a 0600
/// token-file fallback for standalone launches — exactly the <see cref="ShellCoreEndpoint"/> scheme.
/// Every dial authenticates with HMAC-SHA256(nonce, capability), so an unprivileged peer cannot drive
/// a select/query.</para>
/// </summary>
public static class FilerControlEndpoint
{
    private const string DirEnv = "BEVEL_FILER_DIR";
    private const string TokenEnv = "BEVEL_FILER_TOKEN";

    /// <summary>The capability string every dial presents in the handshake (HMAC'd with the nonce).</summary>
    public const string Capability = "filer-control";

    /// <summary>The rendezvous directory: env-published (so spawned Filers inherit it) or the default
    /// under the temp dir, co-located with the other bevel runtime sockets.</summary>
    public static string Dir =>
        Environment.GetEnvironmentVariable(DirEnv) is { Length: > 0 } d
            ? d
            : BevelRuntimeDir.FilersDir; // shared runtime root; off %TEMP% on Windows (bevel-ncfp.2)

    private static string TokenPath => Path.Combine(Dir, "channel.token");

    /// <summary>This process's control-socket path within the rendezvous dir (keyed by pid so a crashed
    /// Filer's leftover file never collides with its successor).</summary>
    public static string SocketPathForPid(int pid) => Path.Combine(Dir, $"filer-{pid}.sock");

    /// <summary>Creates the rendezvous dir 0700 (owner-only — it holds an authenticated control channel).</summary>
    public static void EnsureDir()
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(Dir);
        else
            Directory.CreateDirectory(Dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>
    /// The shared channel nonce, resolved identically on both sides: from env
    /// (<c>BEVEL_FILER_TOKEN</c>, set by the taskbar before it spawns Filers), else from the 0600
    /// token file, else minted and persisted here (first caller wins the create race; a loser reads back
    /// the winner's bytes). Also republishes the value into this process's env so any child it spawns
    /// inherits the same secret.
    /// </summary>
    public static byte[] ResolveNonce()
    {
        if (Environment.GetEnvironmentVariable(TokenEnv) is { Length: > 0 } env)
            return Convert.FromHexString(env);

        EnsureDir();

        // Try to claim the token file atomically; if it already exists, read the existing nonce.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var fs = new FileStream(TokenPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var minted = RandomNumberGenerator.GetBytes(16);
                var hex = Convert.ToHexString(minted);
                using (var w = new StreamWriter(fs)) w.Write(hex);
                Harden(TokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                Environment.SetEnvironmentVariable(TokenEnv, hex);
                return minted;
            }
            catch (IOException) when (File.Exists(TokenPath))
            {
                // Lost the create race (or a prior run left it) — read the persisted nonce.
                try
                {
                    var hex = File.ReadAllText(TokenPath).Trim();
                    if (hex.Length > 0)
                    {
                        Environment.SetEnvironmentVariable(TokenEnv, hex);
                        return Convert.FromHexString(hex);
                    }
                }
                catch (IOException) { /* mid-write by the winner — retry the loop once */ }
            }
        }

        // Extremely unlikely: fall back to a process-local nonce so we never throw on the hot path.
        var fallback = RandomNumberGenerator.GetBytes(16);
        Environment.SetEnvironmentVariable(TokenEnv, Convert.ToHexString(fallback));
        return fallback;
    }

    /// <summary>Publishes the rendezvous dir + nonce into this process's env so spawned Filers inherit
    /// the same discovery root and shared secret. Called once by the taskbar before it spawns any
    /// Filer (bevel-uldj); idempotent.</summary>
    public static void PublishForChildren()
    {
        Environment.SetEnvironmentVariable(DirEnv, Dir);
        _ = ResolveNonce(); // mints + sets BEVEL_FILER_TOKEN if not already present
    }

    private static void Harden(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, mode);
    }
}

/// <summary>Which window-coupled operation a <see cref="FilerRequest"/> asks a live Filer to run
/// against its in-process file-manager surface (bevel-uldj).</summary>
public enum FilerCommandKind
{
    /// <summary>Enumerate this Filer's open window ids + its most-recent activation tick (for the
    /// taskbar's frontmost-first ordering).</summary>
    QueryWindows,
    /// <summary>Read a window's controller selection. <see cref="FilerRequest.LocalWindowId"/> null =
    /// this Filer's front (most-recently-activated) window.</summary>
    QuerySelection,
    /// <summary>Set a window's selection. <see cref="FilerRequest.LocalWindowId"/> null = this
    /// Filer's front window.</summary>
    Select,
}

/// <summary>A taskbar→Filer request. Flat envelope with nullable slots (mirrors
/// <see cref="CoreCommand"/>) so the STJ round-trip stays trivial.</summary>
public sealed record FilerRequest(
    FilerCommandKind Kind,
    int? LocalWindowId = null,
    IReadOnlyList<string>? Paths = null);

/// <summary>An Filer→taskbar reply. <see cref="Ok"/>=false carries <see cref="Error"/> (surfaced as
/// an <c>AutomationException</c> on the taskbar side); the query kinds fill the matching slot.</summary>
public sealed record FilerReply(
    bool Ok,
    string? Error = null,
    IReadOnlyList<int>? WindowIds = null,
    /// <summary>QueryWindows: the newest window-activation tick in this Filer, for frontmost-first
    /// ordering across Filers. 0 when the Filer has never been activated.</summary>
    long FocusTicks = 0,
    IReadOnlyList<string>? Paths = null)
{
    public static FilerReply Fail(string error) => new(Ok: false, Error: error);
}

/// <summary>UTF-8 JSON codec for the channel, source-generated (reflection-free, NativeAOT-safe —
/// mirrors <see cref="CoreProtocol"/>). Payloads are tiny and human-driven, so JSON keeps the wire
/// debuggable.</summary>
public static class FilerProtocol
{
    public static byte[] Serialize<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, TypeInfoFor(typeof(T)));

    public static T Deserialize<T>(ReadOnlySpan<byte> utf8) =>
        (T?)JsonSerializer.Deserialize(utf8, TypeInfoFor(typeof(T)))
        ?? throw new InvalidOperationException($"filer-control: null {typeof(T).Name} on the wire");

    private static JsonTypeInfo TypeInfoFor(Type type) =>
        FilerJsonContext.Default.GetTypeInfo(type)
        ?? throw new InvalidOperationException($"filer-control: {type.Name} not registered in FilerJsonContext");
}

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(FilerRequest))]
[JsonSerializable(typeof(FilerReply))]
internal sealed partial class FilerJsonContext : JsonSerializerContext;
