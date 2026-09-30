using System.Diagnostics;
using System.Runtime.InteropServices;
using Bevel.Ipc;
using Bevel.Ipc.V1;

// Live helper probe: Activate Jump windows OR CaptureWindow latency samples.
// Env: BEVEL_HELPER_SOCKET, BEVEL_HELPER_TOKEN (or auto-detect from BevelHelper process).
// Usage:
//   HelperActivateJump [count]              — RestoreAndActivate Jump windows
//   HelperActivateJump capture [count]      — CaptureWindow on listed windows; prints JSON metrics

static (string sock, string token) DetectHelper()
{
    var sockEnv = Environment.GetEnvironmentVariable("BEVEL_HELPER_SOCKET");
    var tokenEnv = Environment.GetEnvironmentVariable("BEVEL_HELPER_TOKEN");
    if (!string.IsNullOrEmpty(sockEnv) && !string.IsNullOrEmpty(tokenEnv))
        return (sockEnv, tokenEnv);

    foreach (var p in Process.GetProcessesByName("BevelHelper"))
    {
        string? sock = null;
        string? token = null;
        try
        {
            var ps = Process.Start(new ProcessStartInfo
            {
                FileName = "ps",
                Arguments = $"eww -p {p.Id} -o command=",
                RedirectStandardOutput = true,
                UseShellExecute = false,
            })!;
            var cmd = ps.StandardOutput.ReadToEnd();
            ps.WaitForExit();
            if (token is null)
            {
                var m = System.Text.RegularExpressions.Regex.Match(cmd, @"BEVEL_HELPER_TOKEN=([0-9A-Fa-f]+)");
                if (m.Success) token = m.Groups[1].Value;
            }
            if (sock is null)
            {
                var m = System.Text.RegularExpressions.Regex.Match(cmd, @"--socket\s+(\S+)");
                if (m.Success) sock = m.Groups[1].Value;
            }
        }
        catch { /* try next */ }

        if (!string.IsNullOrEmpty(sock) && !string.IsNullOrEmpty(token) && File.Exists(sock))
            return (sock, token!);
    }
    throw new InvalidOperationException("No live BevelHelper socket/token found");
}

static double Pct(List<double> xs, double p)
{
    if (xs.Count == 0) return 0;
    xs.Sort();
    var k = Math.Min(xs.Count - 1, Math.Max(0, (int)Math.Round((p / 100.0) * (xs.Count - 1))));
    return Math.Round(xs[k], 1);
}

if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
{
    Console.Error.WriteLine("macOS only");
    return 1;
}

var mode = args.Length > 0 && args[0].Equals("capture", StringComparison.OrdinalIgnoreCase) ? "capture" : "activate";
var countArg = mode == "capture"
    ? (args.Length > 1 && int.TryParse(args[1], out var c1) ? c1 : 6)
    : (args.Length > 0 && int.TryParse(args[0], out var c0) ? c0 : 4);
var count = Math.Clamp(countArg, 1, 20);

var (socket, token) = DetectHelper();
using var client = new HelperClient();
client.Connect(socket, token);
var channel = client.GetChannel() ?? throw new InvalidOperationException("no channel");
var windows = new WindowService.WindowServiceClient(channel);
var auth = HelperClient.BuildAuthMetadata(token, "window");

var list = await windows.ListWindowsAsync(new ListWindowsRequest(), headers: auth);
var ids = list.Windows
    .Where(w => !string.IsNullOrEmpty(w.WindowId) && !w.WindowId.StartsWith("app:", StringComparison.Ordinal))
    .Where(w => w.Frame is { Width: >= 200, Height: >= 150 })
    .Select(w => w.WindowId)
    .Distinct()
    .Take(12)
    .ToList();

if (ids.Count == 0)
{
    Console.WriteLine("{\"ok\":0,\"reason\":\"no-windows\"}");
    return 0;
}

if (mode == "capture")
{
    var times = new List<double>();
    var oks = 0;
    // Warm one call (SCK cold start) then sample.
    _ = await windows.CaptureWindowAsync(new CaptureWindowRequest { WindowId = ids[0], MaxWidth = 240, MaxHeight = 160 }, headers: auth);
    for (var i = 0; i < count; i++)
    {
        var id = ids[i % ids.Count];
        var sw = Stopwatch.StartNew();
        try
        {
            var reply = await windows.CaptureWindowAsync(
                new CaptureWindowRequest { WindowId = id, MaxWidth = 240, MaxHeight = 160 },
                headers: auth);
            sw.Stop();
            times.Add(sw.Elapsed.TotalMilliseconds);
            if (!reply.Png.IsEmpty) oks++;
        }
        catch (Exception ex)
        {
            sw.Stop();
            Console.Error.WriteLine($"capture {id} failed: {ex.Message}");
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
    }
    var okRate = count == 0 ? 0.0 : (double)oks / count;
    Console.WriteLine(
        $"{{\"mode\":\"capture\",\"samples\":{times.Count},\"capture_ok_rate\":{okRate:F3}," +
        $"\"capture_rpc_p50_ms\":{Pct(times, 50)},\"capture_rpc_p95_ms\":{Pct(times, 95)}}}");
    return 0;
}

var activated = 0;
var actTimes = new List<double>();
for (var i = 0; i < count; i++)
{
    var id = ids[i % ids.Count];
    var sw = Stopwatch.StartNew();
    try
    {
        await windows.RestoreAndActivateAsync(new WindowRef { WindowId = id }, headers: auth);
        sw.Stop();
        actTimes.Add(sw.Elapsed.TotalMilliseconds);
        activated++;
        await Task.Delay(200);
    }
    catch (Exception ex)
    {
        sw.Stop();
        Console.Error.WriteLine($"activate {id} failed: {ex.Message}");
    }
}
var actOk = count == 0 ? 1.0 : (double)activated / count;
Console.WriteLine(
    $"{{\"mode\":\"activate\",\"activated\":{activated},\"activate_ok_rate\":{actOk:F3}," +
    $"\"click_rpc_p50_ms\":{Pct(actTimes, 50)},\"windows\":{ids.Count}}}");
return 0;
