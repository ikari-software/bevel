using Bevel.Core;
using Xunit;

namespace Bevel.Core.Tests;

public class AppInfoTests
{
    [Fact]
    public void Identifiers_AreFrozenAtM0()
    {
        // ARCH-01: these are frozen and load-bearing. A change here is intentional.
        Assert.Equal("Bevel", AppInfo.ProductName);
        Assert.Equal("pl.ikari.bevel", AppInfo.BundleId);
        Assert.Equal("bevel", AppInfo.UrlScheme);
    }
}
