using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Bevel.FileManager.Components;

/// <summary>
/// Layout-transparent wrapper placed at the root of every ItemView row template. Its sole job is to
/// carry accessibility semantics the bare Border/StackPanel/Grid roots could not: a ListItem control
/// type, an accessible name (bound to <see cref="ItemViewModel.DisplayName"/>), and a SelectionItem
/// pattern reflecting <see cref="ItemViewModel.IsSelected"/> — so a screen reader announces each row's
/// name and selected state (bevel-6zs6). Before this the file list was a bare ItemsControl of
/// non-control elements and was unusable via assistive tech.
///
/// <see cref="Decorator"/> adds no border/padding and arranges its single child to fill its own bounds,
/// so it is visually and behaviourally invisible — the row templates' layout, the marquee/Hit rect
/// testing (which measures the realized <c>ContentPresenter</c>, not this child), and rename all work
/// unchanged.
/// </summary>
internal sealed class ItemRow : Decorator
{
    protected override AutomationPeer OnCreateAutomationPeer() => new ItemRowAutomationPeer(this);
}

/// <summary>Reports a row as an accessible ListItem whose name is the file's DisplayName and whose
/// SelectionItem state mirrors the view-model's IsSelected. Selection here is Bevel's own rect/marquee
/// model (not an Avalonia <c>Selector</c>), so the built-in <see cref="ListItemAutomationPeer"/> — which
/// reads a parent Selector — cannot be reused; this minimal peer bridges to <see cref="ItemView"/>.</summary>
internal sealed class ItemRowAutomationPeer : ControlAutomationPeer, ISelectionItemProvider
{
    public ItemRowAutomationPeer(ItemRow owner) : base(owner) { }

    private ItemViewModel? Vm => Owner.DataContext as ItemViewModel;
    private ItemView? View => Owner.FindAncestorOfType<ItemView>();

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;

    protected override string GetNameCore()
    {
        var name = base.GetNameCore();   // AutomationProperties.Name (bound to DisplayName) when applied
        return string.IsNullOrEmpty(name) ? Vm?.DisplayName ?? string.Empty : name;
    }

    // ── ISelectionItemProvider ────────────────────────────────────────
    public bool IsSelected => Vm?.IsSelected == true;

    // Bevel's selection isn't an ISelectionProvider container; a null container is valid and macOS
    // AX still surfaces IsSelected (AXSelected) from the item pattern.
    public ISelectionProvider? SelectionContainer => null;

    public void Select()
    {
        if (Vm is { } vm) View?.SelectPath(vm.Path);
    }

    // Single-item actuation: focusing/selecting a row moves selection to it. Bevel exposes no public
    // "add one path to the current selection" primitive, so AddToSelection degrades to Select and
    // RemoveFromSelection is a no-op rather than a fabricated behaviour.
    public void AddToSelection() => Select();
    public void RemoveFromSelection() { }
}

/// <summary>The Filer file list. A trivial <see cref="ItemsControl"/> subclass that only overrides its
/// automation peer to report a List control type, so the rows' ListItem peers sit under a proper List
/// container. No behavioural change — virtualization, panels, templates and selection are untouched.</summary>
internal sealed class ItemList : ItemsControl
{
    // Avalonia resolves a control's default ControlTheme/template by its StyleKey (exact type). Without
    // this, the ItemList subclass would find no ItemsControl theme, get no template/ItemsPresenter, and
    // realize no containers — so keep using ItemsControl's theme.
    protected override System.Type StyleKeyOverride => typeof(ItemsControl);

    protected override AutomationPeer OnCreateAutomationPeer() => new ItemListAutomationPeer(this);
}

internal sealed class ItemListAutomationPeer : ItemsControlAutomationPeer
{
    public ItemListAutomationPeer(ItemsControl owner) : base(owner) { }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;
}
