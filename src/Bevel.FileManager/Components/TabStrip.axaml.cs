using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace Bevel.FileManager.Components;

/// <summary>
/// Win2000-style row of tab buttons backing FileManagerWindow's tabbed browsing (bevel-6j9).
/// Purely visual — it has no notion of FileManagerController or VfsPath; the window maps each
/// tab's opaque <see cref="Guid"/> id to its own controller and drives <see cref="AddTab"/>,
/// <see cref="RemoveTab"/>, <see cref="SetHeader"/>, and <see cref="SetActive"/> as tabs are
/// opened, navigated, switched, and closed. Always shows at least the "+" affordance; the close
/// ("x") button on a tab is hidden while it is the only tab (last-tab guard lives in the window,
/// this only reflects it visually).
/// </summary>
public partial class TabStrip : UserControl
{
    private sealed class TabEntry
    {
        public required Guid Id { get; init; }
        public required Border Container { get; init; }
        public required TextBlock HeaderText { get; init; }
        public required Button CloseButton { get; init; }
    }

    private static readonly IBrush ActiveBrush = Brushes.White;
    private static readonly IBrush InactiveBrush = new SolidColorBrush(Color.Parse("#D4D0C8"));

    private readonly List<TabEntry> _entries = new();
    private Guid? _activeId;

    /// <summary>Raised when the user clicks a tab's body (not its close button).</summary>
    public event Action<Guid>? TabSelected;

    /// <summary>Raised when the user clicks a tab's close ("x") button.</summary>
    public event Action<Guid>? TabCloseRequested;

    /// <summary>Raised when the user clicks the trailing "+" button.</summary>
    public event Action? NewTabRequested;

    public TabStrip()
    {
        InitializeComponent();
        NewTabButton.Click += (_, _) => NewTabRequested?.Invoke();
    }

    public int Count => _entries.Count;

    /// <summary>Adds a new tab with the given header and returns its id — an opaque token to pass
    /// back to <see cref="RemoveTab"/>/<see cref="SetHeader"/>/<see cref="SetActive"/>.</summary>
    public Guid AddTab(string header)
    {
        var id = Guid.NewGuid();

        var headerText = new TextBlock
        {
            Text = header,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 2, 4, 2),
        };
        var closeButton = new Button
        {
            Content = "×", // ×
            Width = 16,
            Height = 16,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            Focusable = false,
        };
        // The bare "×" glyph is meaningless to a screen reader — give it a real name (bevel-6zs6).
        Avalonia.Automation.AutomationProperties.SetName(closeButton, $"Close tab {header}");
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(headerText);
        content.Children.Add(closeButton);

        var container = new Border
        {
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1, 1, 1, 0),
            Margin = new Thickness(0, 0, 1, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = content,
        };
        Avalonia.Automation.AutomationProperties.SetName(container, header);   // the tab's accessible name

        var entry = new TabEntry { Id = id, Container = container, HeaderText = headerText, CloseButton = closeButton };
        _entries.Add(entry);

        // The close button marks pointer events handled so a click on it doesn't also select
        // the tab (it's inside the tab's own selection surface).
        closeButton.AddHandler(PointerPressedEvent, (_, e) => e.Handled = true, RoutingStrategies.Tunnel);
        closeButton.Click += (_, e) => { e.Handled = true; TabCloseRequested?.Invoke(id); };
        container.PointerPressed += (_, e) => { if (!e.Handled) TabSelected?.Invoke(id); };

        TabsPanel.Children.Add(container);
        UpdateVisuals();
        return id;
    }

    /// <summary>Removes the tab with the given id, if present.</summary>
    public void RemoveTab(Guid id)
    {
        var entry = _entries.FirstOrDefault(e => e.Id == id);
        if (entry is null) return;

        TabsPanel.Children.Remove(entry.Container);
        _entries.Remove(entry);
        if (_activeId == id) _activeId = null;
        UpdateVisuals();
    }

    /// <summary>Updates the visible label of the tab with the given id (no-op if not found).</summary>
    public void SetHeader(Guid id, string header)
    {
        var entry = _entries.FirstOrDefault(e => e.Id == id);
        if (entry is null) return;
        entry.HeaderText.Text = header;
        // Keep the accessible names in sync with the visible label (bevel-6zs6).
        Avalonia.Automation.AutomationProperties.SetName(entry.Container, header);
        Avalonia.Automation.AutomationProperties.SetName(entry.CloseButton, $"Close tab {header}");
    }

    /// <summary>Reads back a tab's current label — used by tests and diagnostics.</summary>
    public string? GetHeader(Guid id) => _entries.FirstOrDefault(e => e.Id == id)?.HeaderText.Text;

    /// <summary>Marks the tab with the given id as the active one (bold label, white background
    /// matching the content pane below it, matching the rest of the strip otherwise).</summary>
    public void SetActive(Guid id)
    {
        _activeId = id;
        UpdateVisuals();
    }

    private void UpdateVisuals()
    {
        var hideClose = _entries.Count <= 1; // keep at least one tab open (window enforces it; this just hides the affordance)
        foreach (var entry in _entries)
        {
            var isActive = entry.Id == _activeId;
            entry.Container.Background = isActive ? ActiveBrush : InactiveBrush;
            entry.HeaderText.FontWeight = isActive ? FontWeight.Bold : FontWeight.Normal;
            entry.CloseButton.IsVisible = !hideClose;
        }
    }
}
