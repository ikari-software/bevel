using System;
using System.IO;

namespace Bevel.Core.Updates;

/// <summary>
/// When the last update check happened, persisted so the interval is wall-clock rather than
/// per-launch. Without this, a shell relaunched more often than the interval would check on every
/// start — which is exactly the pattern during development and immediately after an update.
///
/// A plain file next to <c>settings.db</c> rather than a settings key, for two reasons: the settings
/// blob is polled every 750 ms by every peer, so writing a timestamp into it would wake them all for
/// something none of them care about; and the runtime dir is <c>/tmp</c> on macOS, which does not
/// survive a reboot.
/// </summary>
public sealed class UpdateCheckState
{
    private readonly string _path;

    public UpdateCheckState(string configDir) =>
        _path = Path.Combine(configDir, "update-check.txt");

    /// <summary>The last recorded check, or null when there has never been one — or when the record is
    /// unreadable or corrupt, which is treated as "never" so a bad file makes the shell check again
    /// rather than never check.</summary>
    public DateTimeOffset? LastCheck
    {
        get
        {
            try
            {
                if (!File.Exists(_path)) return null;
                var text = File.ReadAllText(_path).Trim();
                return DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
    }

    /// <summary>Records a check. Failure to write is swallowed: the cost is re-checking sooner than
    /// intended, which is strictly better than an update checker that can crash the core process.</summary>
    public void Record(DateTimeOffset when)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, when.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
