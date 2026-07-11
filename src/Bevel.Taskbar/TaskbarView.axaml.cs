using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Bevel.Core;
using Bevel.Pal.Abstractions;

namespace Bevel.Taskbar;

public partial class TaskbarView : UserControl
{
    private StartMenu? _startMenu;
    private IAppEnvironment? _appEnv;
    private IWindowManager? _windowManager;
    private readonly Dictionary<string, Button> _windowButtons = new();
    private int _buttonWidth = 160;

    public TaskbarView() => InitializeComponent();

    public Button StartButtonControl => StartButton;
    public StackPanel WindowButtonAreaControl => WindowButtonArea;
    public ClockWidget ClockControl => Clock;

    /// <summary>
    /// Wires the taskbar to its PAL services. Called by the composition root
    /// (App.axaml.cs) after construction — Bevel.Taskbar cannot reference
    /// concrete PALs (ARCH-03), so the services arrive from outside.
    /// </summary>
    public void Initialize(IAppEnvironment? appEnv, IWindowManager? windowManager, int buttonWidth = 160)
    {
        _appEnv = appEnv;
        _windowManager = windowManager;
        _buttonWidth = buttonWidth;

        if (_windowManager is not null)
        {
            _windowManager.WindowOpened += (_, w) => Dispatcher.UIThread.Post(() => UpsertButton(w));
            _windowManager.WindowClosed += (_, w) => Dispatcher.UIThread.Post(() => RemoveButton(w));
            _windowManager.WindowChanged += (_, w) => Dispatcher.UIThread.Post(() => UpsertButton(w));
            _windowManager.ForegroundChanged += (_, w) => Dispatcher.UIThread.Post(() => UpsertButton(w));
            _ = RefreshWindowButtonsAsync();
        }
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        _startMenu ??= new StartMenu(_appEnv);
        StartButton.Click += OnStartButtonClick;
        // Re-flow button widths whenever the strip resizes (window buttons shrink to fit).
        WindowButtonScroller.SizeChanged += (_, _) => LayoutButtons();
        AddHandler(KeyDownEvent, OnTaskbarKeyDown, RoutingStrategies.Tunnel);
    }

    // ── Window buttons (U11) ────────────────────────────────────────────

    private async Task RefreshWindowButtonsAsync()
    {
        if (_windowManager is null) return;
        try
        {
            var windows = await _windowManager.EnumerateAsync();
            Dispatcher.UIThread.Post(() =>
            {
                var live = new HashSet<string>(windows.Select(w => w.Id.Value));
                foreach (var stale in _windowButtons.Keys.Where(k => !live.Contains(k)).ToList())
                {
                    WindowButtonArea.Children.Remove(_windowButtons[stale]);
                    _windowButtons.Remove(stale);
                }
                foreach (var w in windows) UpsertButton(w);
            });
        }
        catch { /* helper not up yet — reconciliation poll retries */ }
    }

    private void UpsertButton(ForeignWindow w)
    {
        if (string.IsNullOrEmpty(w.Title) && string.IsNullOrEmpty(w.AppId)) return;

        if (!_windowButtons.TryGetValue(w.Id.Value, out var btn))
        {
            btn = new Button
            {
                Height = 24,
                Margin = new Thickness(1, 0),
                Padding = new Thickness(4, 0),
                FontSize = 11,
                // Stretch so the icon+title fill the button and the title elides as it shrinks.
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            var id = w.Id;
            btn.Click += async (_, _) => await OnWindowButtonClick(id);
            _windowButtons[w.Id.Value] = btn;
            WindowButtonArea.Children.Add(btn);
            LayoutButtons();
        }

        // Icon (left) + title (right). Icon set once; title/boldness update live.
        if (btn.Content is not StackPanel panel)
        {
            panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                ClipToBounds = true,
            };
            var img = new Image
            {
                Width = 16,
                Height = 16,
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var txt = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            panel.Children.Add(img);
            panel.Children.Add(txt);
            btn.Content = panel;
        }

        var icon = (Image)panel.Children[0];
        var label = (TextBlock)panel.Children[1];
        label.Text = string.IsNullOrEmpty(w.Title) ? w.AppId : w.Title;

        if (w.IconPng is { Length: > 0 } && icon.Source is null)
        {
            try
            {
                using var ms = new MemoryStream(w.IconPng);
                icon.Source = new Bitmap(ms);
            }
            catch { /* invalid PNG — leave blank */ }
        }

        btn.Tag = w; // latest state, used by the click toggle
        btn.FontWeight = w.IsFocused ? FontWeight.Bold : FontWeight.Normal;
    }

    private void RemoveButton(ForeignWindow w)
    {
        if (_windowButtons.Remove(w.Id.Value, out var btn))
        {
            WindowButtonArea.Children.Remove(btn);
            LayoutButtons();
        }
    }

    /// <summary>
    /// Sizes window buttons Win2000-style: they share the available strip width
    /// (available / count), clamped between a minimum (half the max) and the max
    /// (<see cref="_buttonWidth"/>, from TaskbarButtonWidth). Titles elide as buttons
    /// shrink; below the minimum they hold and the strip clips. Additional display
    /// modes (icon-only tier, fixed width, a user-set minimum) are tracked as backlog.
    /// </summary>
    private void LayoutButtons()
    {
        var count = _windowButtons.Count;
        if (count == 0) return;

        var available = WindowButtonScroller.Bounds.Width;
        if (available <= 0) return; // not laid out yet — SizeChanged will re-run this

        var max = _buttonWidth;                  // max button width (e.g. 160)
        var min = Math.Max(1, _buttonWidth / 2); // shrink floor: half the max
        const double perButtonMargin = 2;        // Margin(1,0) => 2px horizontal
        var width = Math.Clamp((available / count) - perButtonMargin, min, max);

        foreach (var btn in _windowButtons.Values)
            btn.Width = width;
    }

    private async Task OnWindowButtonClick(ForeignWindowId id)
    {
        if (_windowManager is null) return;
        try
        {
            // Classic Win2000 toggle. Minimizing NEVER removes the button — the
            // window stays in the list (bevel-m2.3):
            //   minimized       → restore + raise (a raise alone won't de-miniaturize)
            //   focused (up)    → minimize
            //   otherwise       → activate
            var state = _windowButtons.TryGetValue(id.Value, out var b) && b.Tag is ForeignWindow fw ? fw : null;
            if (state is { IsMinimized: true })
            {
                await _windowManager.RestoreAsync(id);
                await _windowManager.ActivateAsync(id);
            }
            else if (state is { IsFocused: true })
                await _windowManager.MinimizeAsync(id);
            else
                await _windowManager.ActivateAsync(id);
            await RefreshWindowButtonsAsync();
        }
        catch { /* helper unavailable — buttons refresh on next poll */ }
    }

    // ── Start menu ──────────────────────────────────────────────────────

    private async void OnStartButtonClick(object? sender, RoutedEventArgs e)
    {
        if (_startMenu is null) return;
        if (_startMenu.IsOpen) _startMenu.Close();
        else await _startMenu.OpenAsync(StartButton);
    }

    private async void OnTaskbarKeyDown(object? sender, KeyEventArgs e)
    {
        // Ctrl+Esc (Win2000 standard) or Option+Esc (macOS-friendly) opens the menu.
        if (e.Key == Key.Escape && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Alt)))
        {
            if (_startMenu is not null && !_startMenu.IsOpen)
            {
                await _startMenu.OpenAsync(StartButton);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape && _startMenu is { IsOpen: true })
        {
            _startMenu.Close();
            e.Handled = true;
        }
    }
}
