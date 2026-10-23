using Avalonia;
using Avalonia.Headless;
using Bevel.UI.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

// Same reason this assembly's sibling Bevel.Taskbar.Tests is serialized (bevel-hd05): the whole
// assembly shares ONE HeadlessUnitTestSession from the attribute above, and every [AvaloniaFact]
// marshals its body onto that session's single dispatcher thread. xUnit parallelises test COLLECTIONS
// by default and a class with no explicit [Collection] is its own collection, so the default here was
// N classes entering one session at once. The loser of that race throws
//   System.PlatformNotSupportedException at Avalonia.Threading.Dispatcher.PushFrame
//       at Avalonia.Headless.HeadlessUnitTestSession.DispatchCore
// before the test body runs at all (it reports in ~1ms). Being scheduling-dependent it wanders between
// OSes and between tests in one class: TabbedBrowsingTests on Linux (bevel-2nn2), then a DIFFERENT
// test of that same class on windows-latest. Serialising costs almost nothing, since these tests were
// already serialised on that one dispatcher thread — the parallelism was mostly illusory.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace Bevel.UI.Tests;

/// <summary>Boots the real <see cref="Bevel.App.App"/> (incl. the Win2000 theme) headlessly.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<Bevel.App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
