using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Bevel.Core.Vfs;

namespace Bevel.FileManager.Components;

public partial class SearchPane : UserControl
{
    private SearchPaneViewModel? _viewModel;
    private bool _isUpdatingLookIn;

    public SearchPane()
    {
        InitializeComponent();
        ResultsList.DoubleTapped += OnResultsDoubleTapped;
    }

    /// <summary>Set the VFS root and look-in path for this search pane.</summary>
    public void Initialize(VfsRoot vfsRoot, string lookInPath)
    {
        _viewModel = new SearchPaneViewModel(vfsRoot)
        {
            LookInPath = lookInPath,
        };

        DataContext = _viewModel;

        // Populate the look-in combo with common roots
        PopulateLookInCombo(lookInPath);
    }

    /// <summary>Set a new look-in path without reinitializing.</summary>
    public void SetLookInPath(string path)
    {
        if (_viewModel is null) return;

        _isUpdatingLookIn = true;
        try
        {
            _viewModel.LookInPath = path;
            LookInCombo.Text = path;
        }
        finally
        {
            _isUpdatingLookIn = false;
        }
    }

    /// <summary>Event raised when the user navigates to a search result.</summary>
    public event EventHandler<VfsPath>? NavigateTo;

    private void PopulateLookInCombo(string currentPath)
    {
        var items = new List<string>();

        // Add common filesystem roots
        try
        {
            // Current drive / volume
            var root = Path.GetPathRoot(currentPath);
            if (!string.IsNullOrEmpty(root))
                items.Add(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            // Home directory
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
                items.Add(home);

            // Desktop
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            if (!string.IsNullOrEmpty(desktop))
                items.Add(desktop);

            // Documents
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrEmpty(docs))
                items.Add(docs);

            // Downloads (common location)
            var downloads = Path.Combine(home, "Downloads");
            if (Directory.Exists(downloads))
                items.Add(downloads);
        }
        catch
        {
            // Swallow errors gathering special folders
        }

        // Ensure current path is in the list
        if (!string.IsNullOrEmpty(currentPath) && !items.Contains(currentPath))
            items.Insert(0, currentPath);

        LookInCombo.ItemsSource = items.Distinct().ToList();
        LookInCombo.Text = currentPath;

        LookInCombo.SelectionChanged += (_, _) =>
        {
            if (_isUpdatingLookIn) return;
            if (LookInCombo.SelectedItem is string selected && !string.IsNullOrEmpty(selected))
            {
                _viewModel!.LookInPath = selected;
                _isUpdatingLookIn = true;
                LookInCombo.Text = selected;
                _isUpdatingLookIn = false;
            }
        };
    }

    private void OnSearchClick(object? sender, RoutedEventArgs e)
    {
        // Sync the combo text to the ViewModel
        if (_viewModel is not null && !string.IsNullOrWhiteSpace(LookInCombo.Text))
        {
            _viewModel.LookInPath = LookInCombo.Text.Trim();
        }

        _viewModel?.StartSearch();
        SearchButton.IsEnabled = false;
        StopButton.IsEnabled = true;
    }

    private void OnStopClick(object? sender, RoutedEventArgs e)
    {
        _viewModel?.StopSearch();
        SearchButton.IsEnabled = true;
        StopButton.IsEnabled = false;
    }

    private void OnResultsDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ResultsList.SelectedItem is SearchResultItem item && _viewModel is not null)
        {
            NavigateTo?.Invoke(this, item.Path);
        }
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        NameInput.Focus();
    }
}
