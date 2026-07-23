using System.Collections.ObjectModel;

namespace Bevel.Taskbar;

/// <summary>
/// View-model for the Start menu's Programs cascade — a thin projection of the background
/// <see cref="ShellModel"/>. The menu binds <see cref="Programs"/> (populated off-thread at
/// startup) so it opens instantly; each item renders its icon lazily on realization.
/// </summary>
public sealed class StartMenuViewModel : ObservableObject
{
    public StartMenuViewModel(ShellModel model)
    {
        Model = model;
        // Re-raise the model's one-shot loaded latch as a property change so the menu can swap its
        // "(Loading…)" placeholder for "(No programs found)" without reaching into ShellModel.
        model.ProgramsLoadedChanged += () => OnPropertyChanged(nameof(ProgramsLoaded));
    }

    public ShellModel Model { get; }

    public ObservableCollection<ProgramItemViewModel> Programs => Model.Programs;

    /// <summary>The curated left-column list (newest-added + most-frequently-used, capped). The full list
    /// stays on <see cref="Programs"/>, which the "All Programs" flyout uses.</summary>
    public ObservableCollection<ProgramItemViewModel> FrequentPrograms => Model.FrequentPrograms;

    /// <summary>True once the startup enumeration has finished (see <see cref="ShellModel.ProgramsLoaded"/>).</summary>
    public bool ProgramsLoaded => Model.ProgramsLoaded;
}
