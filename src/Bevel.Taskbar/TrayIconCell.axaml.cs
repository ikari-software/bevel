using Avalonia.Controls;

namespace Bevel.Taskbar;

/// <summary>
/// One mirrored notification-area slot (bevel-yduf): the decoded icon when there is one, otherwise a
/// visible placeholder — never a blank cell. Shared by the inline tray strip and the overflow flyout in
/// <c>TaskbarView.axaml</c>, which wire click/keyboard forwarding on the cell itself (its
/// <c>DataContext</c> is the <see cref="TrayItemViewModel"/>, and it is the focusable, automation-named
/// element, so an assistive-technology user reaches the placeholder the same way as an icon).
/// </summary>
public partial class TrayIconCell : UserControl
{
    public TrayIconCell() => InitializeComponent();
}
