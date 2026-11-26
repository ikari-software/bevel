using Avalonia;
using Avalonia.Controls;

namespace Bevel.Taskbar;

/// <summary>
/// The Start menu's type-to-search strip (bevel-pr4l, extracted from StartMenu.axaml): the live query +
/// match count shown above the menu body while the user types with the menu open. One control, two
/// frames switched by <see cref="Avalonia.StyledElement.Classes"/> — "classic" (the Win2000 sunken field)
/// or "luna" (the rounded Luna field). The host sets <see cref="Query"/> / <see cref="CountText"/> per
/// keystroke and toggles <c>IsVisible</c>; both layouts host their own instance.
/// </summary>
public partial class StartSearchStrip : UserControl
{
    public static readonly StyledProperty<string?> QueryProperty =
        AvaloniaProperty.Register<StartSearchStrip, string?>(nameof(Query));

    public static readonly StyledProperty<string?> CountTextProperty =
        AvaloniaProperty.Register<StartSearchStrip, string?>(nameof(CountText));

    /// <summary>The live query text shown in the field.</summary>
    public string? Query
    {
        get => GetValue(QueryProperty);
        set => SetValue(QueryProperty, value);
    }

    /// <summary>The match count ("2 matches", "no matches") shown beside the query.</summary>
    public string? CountText
    {
        get => GetValue(CountTextProperty);
        set => SetValue(CountTextProperty, value);
    }

    public StartSearchStrip()
    {
        InitializeComponent();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // Both frames carry their own text blocks (only one is visible per class); keep both in sync so
        // switching the class mid-search never shows a stale query.
        if (change.Property == QueryProperty)
        {
            ClassicSearchText.Text = Query;
            Blue2001SearchText.Text = Query;
        }
        else if (change.Property == CountTextProperty)
        {
            ClassicSearchCount.Text = CountText;
            Blue2001SearchCount.Text = CountText;
        }
    }
}
