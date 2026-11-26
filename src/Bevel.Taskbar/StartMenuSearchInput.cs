using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Bevel.Core.Search;

namespace Bevel.Taskbar;

/// <summary>
/// The Start-menu controls the search swaps between, handed over once from the host's constructor
/// (bevel-pr4l) so the extracted search class needs no access to the host's private XAML fields.
/// </summary>
internal sealed class StartMenuSearchBody
{
    public required Border LunaLayout { get; init; }
    public required Menu Items { get; init; }
    public required ScrollViewer PinnedScroll { get; init; }
    public required ItemsControl Pinned { get; init; }
    public required MenuItem ProgramsItem { get; init; }
    public required StartSearchResultsView ClassicResults { get; init; }
    public required StartSearchResultsView LunaResults { get; init; }
    public required StartSearchStrip ClassicStrip { get; init; }
    public required StartSearchStrip LunaStrip { get; init; }
}

/// <summary>
/// The Start menu's type-to-search, extracted from <see cref="StartMenu"/> (bevel-pr4l): the query state
/// (<see cref="StartMenuFilter"/>), the key/text-input wiring, the <see cref="SearchSession"/> over the
/// apps provider (bevel-6sk6), and the strip/results visibility swap. <see cref="StartMenu"/> hosts it and
/// forwards its public surface.
///
/// Attachment constraint (unchanged from the inline version): the menu lives in a Popup with its own
/// TopLevel, so the handlers must attach to the popup's OWN root panel AND to the inner <see cref="Menu"/>
/// — the Programs cascade opens a popup of its own, and the Menu is the ancestor Avalonia's menu
/// interaction handler relies on to receive keys from nested submenu rows. <see cref="Attach"/> therefore
/// takes both hosts, and <see cref="AlreadySeen"/> drops the second sighting of one event, or a keystroke
/// passing through both would be applied twice.
///
/// KeyDown rides the TUNNEL phase so Enter/Backspace/Escape are seen BEFORE the Menu's own interaction
/// handler consumes them; printable characters arrive as real text input (bubble), which is what makes
/// digits and accented characters work — a Key-enum decode can't.
/// </summary>
internal sealed class StartMenuSearchInput
{
    // bevel-pr4l review: the host back-channel arrives as delegates, so this class needs no
    // reference to the concrete StartMenu (and no private→internal widenings on it).
    private readonly Func<bool> _isOpen;
    private readonly Action _close;
    private readonly Func<bool> _lunaWired;
    private readonly Action _restoreProgramsPlaceholder;
    private readonly StartMenuViewModel? _vm;
    private readonly StartMenuSearchBody _body;
    private readonly StartMenuFilter _filter = new();
    private readonly ObservableCollection<ProgramItemViewModel> _searchResults = new();
    // bevel-6sk6: the sectioned search session + the current Programs-section hits.
    private SearchSession? _searchSession;
    private IReadOnlyList<SearchHit> _programHits = Array.Empty<SearchHit>();
    private RoutedEventArgs? _lastSearchEvent;

    public StartMenuSearchInput(
        StartMenuViewModel? vm,
        StartMenuSearchBody body,
        Func<bool> isOpen,
        Action close,
        Func<bool> lunaWired,
        Action restoreProgramsPlaceholder)
    {
        _vm = vm;
        _body = body;
        _isOpen = isOpen;
        _close = close;
        _lunaWired = lunaWired;
        _restoreProgramsPlaceholder = restoreProgramsPlaceholder;
        QueryChanged += ApplySearch;
        Cleared += ClearSearch;
        EnterPressed += source => TryLaunchTopMatch(source);
        WireSearchSession();
    }

    /// <summary>True while a typed query is narrowing the menu.</summary>
    public bool IsSearchActive => _filter.IsActive;

    /// <summary>The query typed so far (empty when no search is running).</summary>
    public string SearchQuery => _filter.Query;

    /// <summary>The ranked matches currently shown, best first.</summary>
    public IReadOnlyList<ProgramItemViewModel> SearchResults => _searchResults;

    /// <summary>Raised after the query changed (a typed character or Backspace).</summary>
    public event Action? QueryChanged;

    /// <summary>Raised when Escape drops the query (the menu itself stays open).</summary>
    public event Action? Cleared;

    /// <summary>Raised on Enter/Return while a search is running; the return value says whether the top
    /// match was launched, so the caller can mark the key event handled.</summary>
    public event Func<object?, bool>? EnterPressed;

    /// <summary>
    /// Attaches the key + text handlers to every given host — call with BOTH the popup's root panel and
    /// the inner Menu (see the class doc). KeyDown tunnel + TextInput bubble, handledEventsToo: true,
    /// exactly as the inline wiring did.
    /// </summary>
    public void Attach(params Interactive[] hosts)
    {
        foreach (var host in hosts)
        {
            host.AddHandler(InputElement.KeyDownEvent, OnMenuKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
            host.AddHandler(InputElement.TextInputEvent, OnMenuTextInput, RoutingStrategies.Bubble, handledEventsToo: true);
        }
    }

    private bool Enabled => _isOpen() && _vm is not null;

    /// <summary>
    /// The handlers sit on BOTH the popup root and the inner <see cref="Menu"/>: the Programs cascade opens
    /// a popup of its own, and the Menu is the ancestor Avalonia's own menu interaction handler relies on to
    /// receive keys from nested submenu rows — so covering both means a keystroke is seen wherever focus
    /// sits. When an event passes through both, only the first sighting is acted on.
    /// </summary>
    private bool AlreadySeen(RoutedEventArgs e)
    {
        if (ReferenceEquals(_lastSearchEvent, e)) return true;
        _lastSearchEvent = e;
        return false;
    }

    private void OnMenuTextInput(object? sender, TextInputEventArgs e)
    {
        if (!Enabled) return;
        if (AlreadySeen(e)) return;
        if (!_filter.Append(e.Text)) return;
        QueryChanged?.Invoke();
        e.Handled = true;
    }

    private void OnMenuKeyDown(object? sender, KeyEventArgs e)
    {
        if (!Enabled) return;
        if (AlreadySeen(e)) return;
        switch (e.Key)
        {
            case Key.Back when _filter.IsActive:
                _filter.Backspace();
                QueryChanged?.Invoke();
                e.Handled = true;
                break;
            case Key.Escape when _filter.IsActive:
                _filter.Clear();
                Cleared?.Invoke();
                e.Handled = true;
                break;
            case Key.Enter or Key.Return when _filter.IsActive:
                if (EnterPressed?.Invoke(e.Source) == true) e.Handled = true;
                break;
            default:
                break;   // arrows / Home / End / mnemonics stay with the menu's own navigation
        }
    }

    // bevel-6sk6: one SearchSession over the apps provider feeds BOTH layouts' results views; publishes
    // are marshaled to the UI thread via Dispatcher.Post (background providers answer off-thread).
    private void WireSearchSession()
    {
        if (_vm is null) return;
        _searchSession = new SearchSession(
            new ISearchProvider[] { new AppsSearchProvider(_vm.Model) },
            a => Dispatcher.UIThread.Post(a));
        _searchSession.SectionChanged += OnSearchSectionChanged;
        foreach (var view in new[] { _body.ClassicResults, _body.LunaResults })
        {
            view.RegisterProvider(_searchSession.Providers[0]);
            view.HitActivated += hit => { _ = hit.ActivateAsync(); _close(); };
        }
    }

    /// <summary>
    /// Escape's first meaning while searching: drop the query and restore the full menu. Returns false when
    /// no search is running, which is the host's cue to close the menu instead (see
    /// <c>TaskbarView.OnTaskbarKeyDown</c>) — so one Escape clears, the next closes.
    /// </summary>
    public bool ClearSearchIfActive()
    {
        if (!_filter.IsActive) return false;
        ClearSearch();
        return true;
    }

    /// <summary>
    /// Enter's rule while a search is running: launch the top match — UNLESS the keystroke came from a row
    /// that is itself one of the matches, i.e. the user arrowed focus onto it, in which case that row's own
    /// activation is the right thing and the search stays out of the way.
    ///
    /// Exposed on the host because it is the only honestly testable form of the rule: headless Avalonia
    /// never realizes a submenu's containers, so no test can produce a focused cascade row to raise the
    /// key from.
    /// </summary>
    public bool EnterShouldLaunchTopMatch(object? keySource)
    {
        if (_searchSession?.TopHit is null) return false;
        return keySource is not Control c
            || c.DataContext is not SearchHit focused
            || !_programHits.Contains(focused);
    }

    private bool TryLaunchTopMatch(object? source)
    {
        if (!EnterShouldLaunchTopMatch(source) || _searchSession?.TopHit is not { } top) return false;
        _ = top.ActivateAsync();
        _close();
        return true;
    }

    /// <summary>
    /// Runs the current query through the <see cref="SearchSession"/> (bevel-6sk6) and swaps the live
    /// layout's body to the sectioned results view. The Programs cascade / pinned column are NEVER
    /// re-pointed at results — the results view replaces them for the duration of the search.
    /// </summary>
    private void ApplySearch()
    {
        if (!_filter.IsActive) { ClearSearch(); return; }
        _searchSession?.SetQuery(_filter.Query);

        var luna = _body.LunaLayout.IsVisible;
        _body.Items.IsVisible = !(_filter.IsActive && !luna);
        _body.ClassicResults.IsVisible = _filter.IsActive && !luna;
        _body.PinnedScroll.IsVisible = !(_filter.IsActive && luna);
        _body.LunaResults.IsVisible = _filter.IsActive && luna;
        UpdateSearchStrip();
    }

    /// <summary>A section's hits arrived (marshaled to the UI thread): feed both results views and keep
    /// the Programs hits for Enter/arrow semantics.</summary>
    private void OnSearchSectionChanged(string sectionId, IReadOnlyList<SearchHit> hits)
    {
        _body.ClassicResults.SetSection(sectionId, hits);
        _body.LunaResults.SetSection(sectionId, hits);
        if (sectionId == "programs")
        {
            _programHits = hits;
            _searchResults.Clear();
            foreach (var h in hits)
                if (h.Tag is ProgramItemViewModel p) _searchResults.Add(p);
        }
        UpdateSearchStrip();
    }

    private void UpdateSearchStrip()
    {
        var n = _searchResults.Count;
        var count = n switch { 0 => "no matches", 1 => "1 match", _ => $"{n} matches" };
        _body.ClassicStrip.Query = _filter.Query;
        _body.ClassicStrip.CountText = count;
        _body.LunaStrip.Query = _filter.Query;
        _body.LunaStrip.CountText = count;
        _body.ClassicStrip.IsVisible = _filter.IsActive && !_body.LunaLayout.IsVisible;
        _body.LunaStrip.IsVisible = _filter.IsActive && _body.LunaLayout.IsVisible;
    }

    /// <summary>Drops the query and puts both layouts back the way they were: the results views hide, the
    /// item column / pinned column show again (Luna's pinned column returns to the curated frequent list).</summary>
    public void ClearSearch()
    {
        _filter.Clear();
        _searchSession?.Clear();
        _searchResults.Clear();
        _programHits = Array.Empty<SearchHit>();
        _body.ClassicStrip.IsVisible = false;
        _body.LunaStrip.IsVisible = false;
        _body.ClassicResults.IsVisible = false;
        _body.LunaResults.IsVisible = false;
        _body.ClassicResults.ClearSections();
        _body.LunaResults.ClearSections();
        _body.Items.IsVisible = true;
        _body.PinnedScroll.IsVisible = true;
        if (_vm is null) return;
        if (_lunaWired()) _body.Pinned.ItemsSource = _vm.FrequentPrograms;
        _body.ProgramsItem.IsSubMenuOpen = false;
        _restoreProgramsPlaceholder();
    }
}
