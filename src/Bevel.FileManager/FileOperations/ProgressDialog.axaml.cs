using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Bevel.FileManager.FileOperations;

/// <summary>
/// Win2000-style modeless progress dialog (FM-132).
/// Shows flying-paper animation, from→to path, progress bar with blocks,
/// time-remaining estimate, and Cancel button.
/// </summary>
public partial class ProgressDialog : Window
{
    private FileOpRequest _request = null!;
    private FileOpResult? _result;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private long _bytesTransferred;
    private long _totalBytes;
    private int _itemsDone;
    private int _totalItems;

    // Time-remaining smoothing (5 s sliding window per spec)
    private readonly Queue<(long bytes, TimeSpan elapsed)> _speedSamples = new();
    private const int SpeedWindowSeconds = 5;

    public FileOpResult? Result => _result;
    public bool WasCancelled { get; private set; }

    public ProgressDialog()
    {
        InitializeComponent();
    }

    public ProgressDialog(FileOpRequest request, string fromPath, string toPath)
        : this()
    {
        _request = request;

        Title = _request switch
        {
            CopyRequest => "Copying...",
            MoveRequest => "Moving...",
            DeleteRequest d when d.ToTrash => "Moving to Trash...",
            DeleteRequest => "Deleting...",
            RenameRequest => "Renaming...",
            _ => "Processing...",
        };

        FromToLabel.Text = $"{fromPath}  →  {toPath}";
    }

    /// <summary>
    /// Adopt an already-running operation: subscribe to its <paramref name="progress"/> stream,
    /// show the dialog, and cancel via <paramref name="cts"/> if the user clicks Cancel. The
    /// caller starts <paramref name="opTask"/> (with <paramref name="cts"/>'s token) so it can
    /// decide — after a short delay — whether the op is slow enough to warrant showing this
    /// dialog at all, avoiding a modal flash for instant operations.
    /// </summary>
    public async Task<FileOpResult> AdoptAsync(
        IObservable<FileOpProgress> progress,
        Task<FileOpResult> opTask,
        CancellationTokenSource cts,
        Window owner)
    {
        using var sub = progress.Subscribe(OnProgress);

        // Close the dialog once the operation finishes.
        _ = opTask.ContinueWith(_ => Dispatcher.UIThread.Post(() => Close()), TaskScheduler.Default);

        await ShowDialog(owner);

        if (WasCancelled) cts.Cancel();

        // The service returns a (possibly Cancelled) result; on hard cancel it may throw
        // OperationCanceledException, which the caller's mutation wrapper handles.
        _result = await opTask;
        return _result;
    }

    private void OnProgress(FileOpProgress progress)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _bytesTransferred = progress.BytesTransferred;
            _totalBytes = progress.TotalBytes;
            _itemsDone = progress.CurrentFileIndex;
            _totalItems = progress.TotalFiles;

            // Update progress bar
            if (_totalBytes > 0)
            {
                var pct = (double)_bytesTransferred / _totalBytes * 100;
                ProgressBar.Value = pct;
                PercentLabel.Text = $"{pct:F0}%";
            }
            else if (_totalItems > 0)
            {
                var pct = (double)_itemsDone / _totalItems * 100;
                ProgressBar.Value = pct;
                PercentLabel.Text = $"{pct:F0}%";
            }

            // Current file
            if (!string.IsNullOrEmpty(progress.CurrentFileName))
                CurrentFileLabel.Text = progress.CurrentFileName;

            // Time remaining estimate
            UpdateTimeEstimate();

            // Auto-close on completion
            if (progress.Status is FileOpStatus.Completed or FileOpStatus.Failed
                or FileOpStatus.Cancelled)
            {
                CancelButton.Content = "Close";
            }
        });
    }

    private void UpdateTimeEstimate()
    {
        var now = _elapsed.Elapsed;

        // Add speed sample
        _speedSamples.Enqueue((_bytesTransferred, now));
        while (_speedSamples.Count > 0 &&
               (now - _speedSamples.Peek().elapsed).TotalSeconds > SpeedWindowSeconds)
            _speedSamples.Dequeue();

        if (_speedSamples.Count < 2 || _totalBytes <= 0) return;

        var oldest = _speedSamples.Peek();
        var newest = _speedSamples.Last();
        var timeDelta = newest.elapsed - oldest.elapsed;
        var bytesDelta = newest.bytes - oldest.bytes;

        if (timeDelta.TotalSeconds < 0.5 || bytesDelta <= 0)
        {
            TimeLabel.Text = "";
            return;
        }

        var bytesPerSecond = bytesDelta / timeDelta.TotalSeconds;
        var remainingBytes = _totalBytes - _bytesTransferred;
        var remainingSeconds = remainingBytes / bytesPerSecond;

        // Only show after 3 s elapsed and >10 s remaining (matching Win2000 temperament)
        if (_elapsed.Elapsed.TotalSeconds < 3 || remainingSeconds < 10)
        {
            TimeLabel.Text = "";
            return;
        }

        TimeLabel.Text = remainingSeconds switch
        {
            < 60 => $"About {remainingSeconds:F0} sec remaining",
            < 3600 => $"About {remainingSeconds / 60:F0} min {remainingSeconds % 60:F0} sec remaining",
            _ => $"About {remainingSeconds / 3600:F0} hr {remainingSeconds % 3600 / 60:F0} min remaining",
        };
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        if (CancelButton.Content is string s && s == "Close")
        {
            Close();
            return;
        }

        WasCancelled = true;
        CancelButton.IsEnabled = false;
        CancelButton.Content = "Cancelling...";
    }
}