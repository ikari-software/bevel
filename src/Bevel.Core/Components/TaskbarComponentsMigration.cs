using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Bevel.Core.Components;

/// <summary>Stable type-ids for Bevel's own components. Instances persist against these strings.</summary>
public static class TaskbarComponentTypes
{
    public const string Start = "run.bevel.start";
    public const string WindowStrip = "run.bevel.window-strip";
    public const string Stack = "run.bevel.stack";
    public const string Tray = "run.bevel.tray";
    public const string Clock = "run.bevel.clock";
    public const string ShowDesktop = "run.bevel.show-desktop";
    public const string Spacer = "run.bevel.spacer";
}

/// <summary>
/// One-time fold of the legacy flat taskbar keys into an ordered component list (spec §5.2). Reads the
/// legacy keys and leaves them in place: they stay readable for one release so a downgrade does not
/// brick someone's bar. Keys that describe the BAR (rows, locked, opacity, …) stay on BevelSettings,
/// and TaskbarStartMenuFrequentCount stays put because ShellModel consumes it, not the view.
/// </summary>
public static class TaskbarComponentsMigration
{
    private static string B(bool v) => v ? "true" : "false";
    private static string N(int v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A STABLE id for the <paramref name="ordinal"/>-th instance of <paramref name="typeId"/> this
    /// migration creates. Deliberately NOT <see cref="ComponentInstance.NewId"/> (which is
    /// <c>Guid.NewGuid()</c> and correct for a genuine later user-initiated add): this migration runs
    /// at multiple independent entry points over the SAME unmigrated blob — <c>SettingsService</c>'s
    /// own <c>LoadAsync</c>/<c>ReloadIfChangedAsync</c> on one process, and the static
    /// <c>ProjectBlob</c>/<c>TryProjectBlob</c> a peer uses to decode a core snapshot on another — and
    /// the version-gate only mutates the in-memory settings object, not the raw blob, so the SAME
    /// legacy input can legitimately be migrated more than once before a save ever persists the
    /// result. If ids were random, core and a peer projecting the identical unmigrated blob would
    /// compute DIFFERENT instanceIds for what is supposed to be the same placement — and instanceId is
    /// the key that per-instance settings, surface slot ownership, the component health budget, and
    /// the bus's peer→instance binding all hang off. A hash of the (typeId, ordinal) pair makes this
    /// migration a pure function of its input: the same legacy settings always fold to the same ids,
    /// everywhere, no matter how many times or where this runs before the first save lands. Keeps
    /// <see cref="ComponentInstance.NewId"/>'s 12-lowercase-hex-character shape because these ids are
    /// persisted into user settings — changing the length later would itself be a migration.
    /// </summary>
    private static string DeterministicId(string typeId, int ordinal)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{typeId}:{ordinal}"));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    public static ComponentInstance[] BuildDefaultList(BevelSettings s)
    {
        var list = new List<ComponentInstance>();

        list.Add(new ComponentInstance(DeterministicId(TaskbarComponentTypes.Start, 0), TaskbarComponentTypes.Start,
            new Dictionary<string, string>
            {
                ["label"] = s.TaskbarStartLabel,
                ["badgeFullDetail"] = B(s.StartBadgeFullDetail),
            },
            Visible: s.TaskbarShowStart));

        list.Add(new ComponentInstance(DeterministicId(TaskbarComponentTypes.WindowStrip, 0), TaskbarComponentTypes.WindowStrip,
            new Dictionary<string, string>
            {
                ["buttonWidth"] = N(s.TaskbarButtonWidth),
                ["buttonWidthMode"] = s.TaskbarButtonWidthMode.ToString(),
                ["minButtonWidth"] = N(s.TaskbarMinButtonWidth),
                ["grouping"] = s.TaskbarGrouping.ToString(),
                ["buttonLabels"] = s.TaskbarButtonLabels.ToString(),
                ["middleClickCloses"] = B(s.TaskbarMiddleClickCloses),
                ["reclickMinimize"] = s.TaskbarReclickMinimize.ToString(),
                ["windowSort"] = s.TaskbarWindowSort.ToString(),
                ["windowlessAppsLast"] = B(s.WindowlessAppsLast),
            },
            Visible: true));

        var stackOrdinal = 0;
        foreach (var folder in s.TaskbarStacks)
            list.Add(new ComponentInstance(DeterministicId(TaskbarComponentTypes.Stack, stackOrdinal++), TaskbarComponentTypes.Stack,
                new Dictionary<string, string> { ["folder"] = folder }, Visible: true));

        list.Add(new ComponentInstance(DeterministicId(TaskbarComponentTypes.Tray, 0), TaskbarComponentTypes.Tray,
            new Dictionary<string, string>
            {
                ["overflowCap"] = N(s.TaskbarTrayOverflowCap),
                ["consolidateMenuBar"] = B(s.TaskbarConsolidateMenuBar),
            },
            Visible: true));

        list.Add(new ComponentInstance(DeterministicId(TaskbarComponentTypes.Clock, 0), TaskbarComponentTypes.Clock,
            new Dictionary<string, string>
            {
                ["showSeconds"] = B(s.TaskbarClockShowSeconds),
                ["showDate"] = B(s.TaskbarClockShowDate),
                ["use24Hour"] = B(s.TaskbarClock24Hour),
            },
            Visible: s.TaskbarShowClock));

        list.Add(new ComponentInstance(DeterministicId(TaskbarComponentTypes.ShowDesktop, 0), TaskbarComponentTypes.ShowDesktop,
            new Dictionary<string, string>(), Visible: s.TaskbarShowDesktopButton));

        return list.ToArray();
    }
}
