using Avalonia.Headless.XUnit;
using Bevel.App;
using Xunit;

namespace Bevel.UI.Tests;

public class MainWindowSmokeTests
{
    [AvaloniaFact]
    public void MainWindow_Constructs_AndShows()
    {
        var window = new MainWindow();
        window.Show();

        Assert.Equal("Bevel — M0", window.Title);
    }
}
