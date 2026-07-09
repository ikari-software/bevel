namespace Bevel.FileManager.FileOperations;

/// <summary>
/// A handle to a single file operation returned by <see cref="FileOperationService.Begin"/>.
/// Each operation gets its own handle and its own cancellation source, so one window cancelling
/// its transfer never disturbs another window's in-flight operation (bevel-var / review AD3-#9,
/// which flagged the shared-mutable-CTS aliasing hazard now that multi-window ops are enabled).
/// </summary>
public interface IFileOpHandle
{
    /// <summary>Stable id shared by this operation's progress events, result, and undo entry.</summary>
    string OperationId { get; }

    /// <summary>
    /// Completes with the operation's result. It does not fault for expected outcomes —
    /// cancellation surfaces as <c>Cancelled</c> item results, not an exception.
    /// </summary>
    Task<FileOpResult> Completion { get; }

    /// <summary>
    /// Requests cancellation of THIS operation only. The file currently in progress finishes,
    /// then processing stops. Safe to call at any time, including after the operation has
    /// already completed (it is then a no-op).
    /// </summary>
    void Cancel();
}
