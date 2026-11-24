using Avalonia.Controls;
using Avalonia.Interactivity;
using Bevel.Core.Search;

namespace Bevel.Taskbar;

/// <summary>
/// The Start menu's sectioned results panel (bevel-6sk6): one virtualized list whose rows are section
/// headers (<see cref="SearchSectionHeader"/>) and hits (<see cref="SearchHit"/>), rebuilt in provider
/// order on every <see cref="SearchSession.SectionChanged"/>. Shown in place of the menu body while a
/// search is active, in either layout (each layout hosts its own instance of this control, fed by the
/// same session).
/// </summary>
public partial class StartSearchResultsView : UserControl
{
    private readonly List<(ISearchProvider Provider, IReadOnlyList<SearchHit> Hits)> _sections = new();

    /// <summary>The flat row list bound to the ListBox (headers + hits).</summary>
    public System.Collections.ObjectModel.ObservableCollection<object> Rows { get; } = new();

    /// <summary>Raised when a hit row is clicked; the Start menu also activates via Enter on TopHit.</summary>
    public event Action<SearchHit>? HitActivated;

    public StartSearchResultsView()
    {
        InitializeComponent();
        ResultsList.ItemsSource = Rows;
    }

    /// <summary>Registers a section (provider order fixes the display order). Called once per provider at wiring.</summary>
    public void RegisterProvider(ISearchProvider provider)
    {
        _sections.Add((provider, Array.Empty<SearchHit>()));
        Rebuild();
    }

    /// <summary>Replaces one section's hits and rebuilds the rows.</summary>
    public void SetSection(string sectionId, IReadOnlyList<SearchHit> hits)
    {
        var i = _sections.FindIndex(s => s.Provider.SectionId == sectionId);
        if (i < 0) return;
        _sections[i] = (_sections[i].Provider, hits);
        Rebuild();
    }

    /// <summary>Empties every section (a cleared query shows the "(No matches)" placeholder row).</summary>
    public void ClearSections()
    {
        for (var i = 0; i < _sections.Count; i++)
            _sections[i] = (_sections[i].Provider, Array.Empty<SearchHit>());
        Rebuild();
    }

    private void Rebuild()
    {
        Rows.Clear();
        var any = false;
        foreach (var (provider, hits) in _sections.OrderBy(s => s.Provider.Order))
        {
            if (hits.Count == 0) continue;
            any = true;
            Rows.Add(new SearchSectionHeader(provider.SectionTitle));
            foreach (var hit in hits.OrderBy(h => h.Rank))
                Rows.Add(hit);
        }
        if (!any) Rows.Add(new SearchSectionHeader("(No matches)"));
    }

    private void OnHitClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is SearchHit hit)
        {
            HitActivated?.Invoke(hit);
            e.Handled = true;
        }
    }
}
