using Bevel.App.Supervision;
using Xunit;

namespace Bevel.Taskbar.Tests;

public class QuitRequestTests : IDisposable
{
    public QuitRequestTests() => QuitRequest.Clear();
    public void Dispose() => QuitRequest.Clear();

    [Fact]
    public void Write_Exists_Clear_round_trips()
    {
        Assert.False(QuitRequest.Exists());
        QuitRequest.Write();
        Assert.True(QuitRequest.Exists());
        Assert.True(File.Exists(QuitRequest.Path));
        QuitRequest.Clear();
        Assert.False(QuitRequest.Exists());
    }
}
