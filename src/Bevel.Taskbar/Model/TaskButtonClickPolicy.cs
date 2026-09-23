using Bevel.Core;

namespace Bevel.Taskbar;

/// <summary>
/// Click semantics for the task strip (bevel-au94) — currently just "does clicking the ACTIVE window's
/// own button minimize it?".
///
/// It lives in one small mutable holder rather than on each <see cref="TaskItemViewModel"/> because the
/// setting must live-apply to buttons that already exist: the view writes the mode
/// once, and every button — including the ones created later by <c>ShellModel</c>, which has no settings of
/// its own — reads the same object. <see cref="Shared"/> is the taskbar process's instance; tests construct
/// their own so nothing is asserted against global state.
/// </summary>
public sealed class TaskButtonClickPolicy
{
    /// <summary>The taskbar process's live policy. <c>TaskbarView</c> pushes the setting into it on
    /// initialize and on every settings-changed apply; buttons read it at click time.</summary>
    public static TaskButtonClickPolicy Shared { get; } = new();

    /// <summary>What a click on the focused window's button does. Default is the classic Win2000 toggle.</summary>
    public TaskbarReclickMinimize Mode { get; set; } = TaskbarReclickMinimize.Click;

    /// <summary>
    /// "Is the minimize modifier held right now?" — queried at click time, never remembered from an
    /// event. The taskbar is non-activating, so keyboard events don't route to it and (verified,
    /// bevel-ww71) pointer events over its popups carry no modifier flags; the platform's live,
    /// focus-independent modifier query is the only source that answers for both a mouse click and a
    /// keyboard Space/Enter activation. Replaceable in tests.
    /// </summary>
    public Func<bool> ModifierHeld { get; set; } = DefaultModifierHeld;

    /// <summary>Live Option (macOS) / Alt (Windows) state. Other platforms have no live query wired yet,
    /// so they report false — <see cref="TaskbarReclickMinimize.OptionClick"/> is macOS/Windows only.</summary>
    internal static bool DefaultModifierHeld() =>
        OperatingSystem.IsMacOS() ? TaskbarNative.OptionKeyDown()
        : OperatingSystem.IsWindows() && WindowsTaskbarNative.AltKeyDown();

    /// <summary>The minimize modifier in this platform's vocabulary, for the accessible status text.</summary>
    public static string ModifierName => OperatingSystem.IsMacOS() ? "Option" : "Alt";

    /// <summary>Whether a click landing on the FOCUSED window's button should minimize it rather than
    /// re-activate it. The minimized→restore and unfocused→activate arms of the toggle are unaffected.</summary>
    public bool MinimizesFocusedWindow() => Mode switch
    {
        TaskbarReclickMinimize.Never => false,
        TaskbarReclickMinimize.OptionClick => ModifierHeld(),
        _ => true,
    };

    /// <summary>The gesture this policy advertises for minimize, or null when it advertises none — drives
    /// the button's accessible <see cref="TaskItemViewModel.StatusText"/> so it never promises an action
    /// the click won't perform.</summary>
    public string? MinimizeGestureHint => Mode switch
    {
        TaskbarReclickMinimize.Never => null,
        TaskbarReclickMinimize.OptionClick => $"{ModifierName}-click to minimize",
        _ => "click to minimize",
    };
}
