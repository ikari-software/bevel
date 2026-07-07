using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Bevel.FileManager.Components;

/// <summary>
/// Win2000 "Search for Files or Folders" query pane. This control ONLY collects a query and
/// raises <see cref="SearchRequested"/> — it never walks the VFS itself. The host window owns a
/// <see cref="FileOperations.SearchService"/>, runs the search against the current folder's
/// subtree when the event fires, and renders matches in the main item view; it then reports the
/// outcome back here via <see cref="SetResultCount"/> / <see cref="SetStatus"/> so the pane can
/// show a status line.
/// </summary>
public partial class SearchPane : UserControl
{
    public SearchPane()
    {
        InitializeComponent();
    }

    /// <summary>The current text in the name-query box.</summary>
    public string NameQuery
    {
        get => NameInput.Text ?? string.Empty;
        set => NameInput.Text = value;
    }

    /// <summary>Raised when the user asks to search (Search Now button or Enter in the name box).
    /// The event argument is the trimmed query text.</summary>
    public event EventHandler<string>? SearchRequested;

    /// <summary>Raised when the user clicks the pane's close ("x") button.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Show a plain status message (e.g. "Searching…", or an error).</summary>
    public void SetStatus(string text) => StatusText.Text = text;

    /// <summary>Show a friendly "N result(s) found" summary once a search completes.</summary>
    public void SetResultCount(int count) => StatusText.Text = count switch
    {
        0 => "No results found.",
        1 => "1 result found.",
        _ => $"{count} results found.",
    };

    /// <summary>Clear the query and status — useful when the pane is hidden/reopened.</summary>
    public void Reset()
    {
        NameQuery = string.Empty;
        StatusText.Text = string.Empty;
    }

    private void OnSearchClick(object? sender, RoutedEventArgs e) => RaiseSearchRequested();

    private void OnNameInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            RaiseSearchRequested();
            e.Handled = true;
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void RaiseSearchRequested()
    {
        var query = NameQuery.Trim();
        if (query.Length == 0)
        {
            SetStatus("Please specify a name to search for.");
            return;
        }
        SearchRequested?.Invoke(this, query);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        NameInput.Focus();
    }
}
