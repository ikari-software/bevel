using Avalonia.Controls;

namespace Bevel.FileManager.Components;

public partial class StatusBar : UserControl
{
    public StatusBar() => InitializeComponent();

    public void UpdateObjectCount(int count)
    {
        ObjectCountText.Text = count == 1
            ? "1 object"
            : $"{count} object(s)";
    }

    /// <summary>Show the current selection (Win2000 shows "N object(s) selected" + size).</summary>
    public void UpdateSelection(int count, long bytes)
    {
        ObjectCountText.Text = count == 1 ? "1 object selected" : $"{count} object(s) selected";
        TotalSizeText.Text = FormatSize(bytes);
    }

    public void UpdateTotalSize(long bytes)
    {
        TotalSizeText.Text = FormatSize(bytes);
    }

    public void UpdateNamespaceZone(string zone)
    {
        NamespaceZoneText.Text = zone;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes == 0) return string.Empty;
        if (bytes < 1024) return $"{bytes} bytes";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}
