using System.Globalization;

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

    public static ComponentInstance[] BuildDefaultList(BevelSettings s)
    {
        var list = new List<ComponentInstance>();

        list.Add(new ComponentInstance(ComponentInstance.NewId(), TaskbarComponentTypes.Start,
            new Dictionary<string, string>
            {
                ["label"] = s.TaskbarStartLabel,
                ["badgeFullDetail"] = B(s.StartBadgeFullDetail),
            },
            Visible: s.TaskbarShowStart));

        list.Add(new ComponentInstance(ComponentInstance.NewId(), TaskbarComponentTypes.WindowStrip,
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

        foreach (var folder in s.TaskbarStacks)
            list.Add(new ComponentInstance(ComponentInstance.NewId(), TaskbarComponentTypes.Stack,
                new Dictionary<string, string> { ["folder"] = folder }, Visible: true));

        list.Add(new ComponentInstance(ComponentInstance.NewId(), TaskbarComponentTypes.Tray,
            new Dictionary<string, string>
            {
                ["overflowCap"] = N(s.TaskbarTrayOverflowCap),
                ["consolidateMenuBar"] = B(s.TaskbarConsolidateMenuBar),
            },
            Visible: true));

        list.Add(new ComponentInstance(ComponentInstance.NewId(), TaskbarComponentTypes.Clock,
            new Dictionary<string, string>
            {
                ["showSeconds"] = B(s.TaskbarClockShowSeconds),
                ["showDate"] = B(s.TaskbarClockShowDate),
                ["use24Hour"] = B(s.TaskbarClock24Hour),
            },
            Visible: s.TaskbarShowClock));

        list.Add(new ComponentInstance(ComponentInstance.NewId(), TaskbarComponentTypes.ShowDesktop,
            new Dictionary<string, string>(), Visible: s.TaskbarShowDesktopButton));

        return list.ToArray();
    }
}
