using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Bevel.Pal.Abstractions;

namespace Bevel.Taskbar;

/// <summary>
/// One permission row in the settings General tab (bevel-ct7): status dot + glyph + label, a grant
/// button (the host opens the right System Settings pane via <see cref="GrantRequested"/>) and a
/// one-line "what this unlocks" hint. Used once per <see cref="ShellPermission"/>. The dot's
/// granted/denied/neutral fills are the same fixed semantic colours the original two rows used;
/// every other colour comes from theme tokens.
/// </summary>
public partial class PermissionRow : UserControl
{
    public static readonly StyledProperty<ShellPermission> PermissionProperty =
        AvaloniaProperty.Register<PermissionRow, ShellPermission>(nameof(Permission));

    /// <summary>Which permission this row shows (set from axaml).</summary>
    public ShellPermission Permission
    {
        get => GetValue(PermissionProperty);
        set => SetValue(PermissionProperty, value);
    }

    /// <summary>Raised when the grant button is clicked; the host maps the permission to a pane.</summary>
    public event EventHandler? GrantRequested;

    public PermissionRow()
    {
        InitializeComponent();
        GrantBtn.Click += (_, _) => GrantRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Applies a probed state: dot colour + glyph + label + button visibility + hint.</summary>
    public void ApplyState(PermissionState state, string grantedHint, string deniedHint)
    {
        switch (state)
        {
            case PermissionState.Granted:
                StatusDot.Background = new SolidColorBrush(Color.Parse("#3FA23F"));
                StatusGlyph.Data = Geometry.Parse("M0,3.5 L2.6,6 L7,0.5");   // check
                StatusText.Text = "Granted";
                GrantBtn.IsVisible = false;
                RowHint.Text = grantedHint;
                break;
            case PermissionState.Denied:
                StatusDot.Background = new SolidColorBrush(Color.Parse("#E24A2E"));
                StatusGlyph.Data = Geometry.Parse("M0,0 L6,6 M6,0 L0,6");     // cross
                StatusText.Text = "Not Granted";
                GrantBtn.IsVisible = true;
                RowHint.Text = deniedHint;
                break;
            default:
                StatusDot.Background = new SolidColorBrush(Color.Parse("#9AA0A6"));
                StatusGlyph.Data = Geometry.Parse("M0,3 L7,3");                // dash
                StatusText.Text = "Unknown";
                GrantBtn.IsVisible = true;
                RowHint.Text = "Checking permission status...";
                break;
        }
    }
}
