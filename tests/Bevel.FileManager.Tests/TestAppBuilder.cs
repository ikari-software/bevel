using Avalonia;
using Avalonia.Headless;
using Bevel.FileManager.Tests;
using Bevel.Tests.Shared;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

// Same reason this assembly's sibling Bevel.Taskbar.Tests is serialized (bevel-hd05): the whole
// assembly shares ONE HeadlessUnitTestSession from the attribute above, and every [AvaloniaFact]
// marshals its body onto that session's single dispatcher thread, so collection parallelism was mostly
// illusory. Serialising does NOT fix the intermittent
//   System.PlatformNotSupportedException at Avalonia.Threading.Dispatcher.PushFrame
//       at Avalonia.Headless.HeadlessUnitTestSession.DispatchCore
// (bevel-2nn2) — that was a stray Dispatcher.UIThread touch from a leftover background thread landing
// in the window between a test's Dispatcher.ResetBeforeUnitTests() and the headless platform binding
// its IDispatcherImpl, which built that test's dispatcher over NullDispatcherImpl. HeadlessDispatcherRoot
// closes that window; see its doc comment.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace Bevel.FileManager.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        // Runs once, ON the headless session thread, before any per-test locator scope exists — the
        // only place a root-scope dispatcher impl gets the right loop thread (bevel-2nn2).
        HeadlessDispatcherRoot.Install();

        return AppBuilder.Configure<Bevel.App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .AfterSetup(_ => HeadlessDispatcherRoot.OnTestSetupCompleted());
    }
}