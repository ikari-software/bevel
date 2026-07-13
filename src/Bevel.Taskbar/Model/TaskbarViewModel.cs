using System.Collections.ObjectModel;

namespace Bevel.Taskbar;

/// <summary>
/// Top-level view-model for <see cref="TaskbarView"/>: a thin projection of the background
/// <see cref="ShellModel"/>. The window-button strip binds <see cref="Windows"/> (an observable
/// collection kept current off the UI thread) via a virtualization-friendly ItemsControl, so
/// the view never builds or mutates buttons by hand. Per-button width (the Win2000 shrink-to-fit
/// sizing, and the XP grow/shrink animation) lives on each <see cref="TaskItemViewModel"/>; the
/// view's layout pass computes the shared target and pushes it to every live button.
/// </summary>
public sealed class TaskbarViewModel : ObservableObject
{
    public TaskbarViewModel(ShellModel model, StartMenuViewModel startMenu)
    {
        Model = model;
        StartMenu = startMenu;
    }

    public ShellModel Model { get; }
    public StartMenuViewModel StartMenu { get; }

    public ObservableCollection<TaskItemViewModel> Windows => Model.Windows;
}
