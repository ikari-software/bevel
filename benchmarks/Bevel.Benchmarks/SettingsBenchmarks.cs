using System.IO;
using Bevel.Core;
using BenchmarkDotNet.Attributes;

namespace Bevel.Benchmarks;

/// <summary>
/// Settings persistence hot path. <see cref="SettingsService.SaveAsync"/> re-serializes the whole
/// typed model (source-generated JSON, prune-to-defaults) and writes it to the SQLite blob + the
/// passive settings.json export. Live-apply sliders/text boxes call this on every tick/keystroke, so
/// it is on an interactive path. Rooted at a private temp dir via the internal test-seam ctor — never
/// the real ~/.config/bevel — and pre-loaded so the connection/table are warm (steady-state save).
/// </summary>
[MemoryDiagnoser]
public class SettingsBenchmarks
{
    private string _dir = null!;
    private SettingsService _svc = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bevel-bench-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _svc = new SettingsService(_dir);
        _svc.LoadAsync().GetAwaiter().GetResult();
        // A handful of non-default values so SerializeRaw writes real keys rather than pruning them all.
        _svc.Current.TaskbarButtonWidth = 200;
        _svc.Current.TaskbarBackgroundColor = "#2A3F5F";
        _svc.Current.TaskbarRows = 2;
    }

    [Benchmark]
    public int Save()
    {
        _svc.SaveAsync().GetAwaiter().GetResult();
        return _svc.Version;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _svc.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
    }
}
