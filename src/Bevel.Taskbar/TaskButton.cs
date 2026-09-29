using Avalonia.Controls.Primitives;

namespace Bevel.Taskbar;

/// <summary>
/// A task-strip button whose pressed (sunken) state is a pure projection of the shell's exclusive
/// focus — <c>IsChecked</c> is bound OneWay to <see cref="TaskItemViewModel.IsFocused"/> /
/// <see cref="TaskGroupViewModel.IsFocused"/> and nothing else may write it (bevel-zk4a).
/// </summary>
/// <remarks>
/// A stock <see cref="ToggleButton"/> flips <c>IsChecked</c> itself on every click. The old code-behind
/// "snapped it back" to <c>IsFocused</c> afterwards, but that snap was a LOCAL value write, which in
/// Avalonia disposes the local-priority binding it overrides — so the first click on any button severed
/// it from focus for good, and the bar stopped showing which window was active. Refusing to self-toggle
/// removes the divergence at the source: a click activates/minimizes through the command, and the pressed
/// state then follows the focus events the shell reports. The style key stays <c>ToggleButton</c> so the
/// live-themable <c>Bevel.Theme.ToggleButton</c> contract (App.axaml) still templates it per skin.
/// </remarks>
public sealed class TaskButton : ToggleButton
{
    protected override Type StyleKeyOverride => typeof(ToggleButton);

    /// <summary>Never self-toggle: pressed means focused, and only the shell decides focus.</summary>
    protected override void Toggle() { }
}
