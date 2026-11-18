using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Bevel.Tests.Shared;

/// <summary>
/// Turns bevel-2nn2's intermittent <c>PlatformNotSupportedException</c> into a diagnosis.
///
/// <para><b>The mechanism, read out of the IL rather than guessed.</b> <c>Dispatcher.PushFrame</c> has
/// exactly ONE <c>PlatformNotSupportedException</c> throw site, and its only condition is
/// <c>_controlledImpl == null</c>:</para>
/// <code>
///   IL_0001: call      Dispatcher::VerifyAccess()
///   IL_0007: ldfld     IControlledDispatcherImpl Dispatcher::_controlledImpl
///   IL_000c: brtrue.s  IL_0014
///   IL_000e: newobj    PlatformNotSupportedException::.ctor()   // &lt;-- the flake
/// </code>
/// <para><c>_controlledImpl</c> is assigned ONCE, in the <c>Dispatcher</c> constructor, from whatever
/// <c>IDispatcherImpl</c> the locator holds at that moment. Avalonia.Headless binds
/// <c>ManagedDispatcherImpl</c> (which IS an <c>IControlledDispatcherImpl</c>) while initializing the
/// platform. So a failing run is one where the <c>Dispatcher.UIThread</c> SINGLETON was constructed
/// before that bind — and once lost it is lost for the whole process, permanently.</para>
///
/// <para><b>Why only one test fails, and a different one each time.</b>
/// <c>HeadlessUnitTestSession.DispatchCore</c> reaches <c>PushFrame</c> only when the test body's Task
/// has NOT already completed synchronously (it checks <c>Status == RanToCompletion</c> first). Almost
/// every test here completes inline, so a poisoned dispatcher is invisible to them; the one test that
/// genuinely yields that run is the one that blows up. That is why the victim wanders between
/// assemblies, OSes and runs — and why serializing the collections made it rarer without fixing it:
/// serialization reduces the chances of an early touch, it does not remove them.</para>
///
/// <para><b>What this test is for.</b> It asserts the invariant directly and costs a microsecond, so
/// the NEXT occurrence is decisive instead of a mystery: if this fails in the same run, the dispatcher
/// really was constructed too early and the hunt is for the first toucher of
/// <c>Dispatcher.UIThread</c> (a plain <c>[Fact]</c>, or a background continuation left over from one —
/// this codebase posts to the UI thread from thread-pool callbacks in several places). If this PASSES
/// while a sibling throws <c>PlatformNotSupportedException</c>, the mechanism above is WRONG and the
/// IL reading needs redoing.</para>
///
/// <para>Linked into every assembly that runs headless Avalonia tests, because the poisoning is
/// per-process and the victim has already moved between two of them.</para>
/// </summary>
public sealed class DispatcherFrameSupport
{
    [AvaloniaFact]
    public void The_ui_dispatcher_can_push_frames()
    {
        var field = typeof(Dispatcher).GetField("_controlledImpl",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(field);   // renamed by an Avalonia upgrade? update this guard, don't delete it

        Assert.NotNull(field!.GetValue(Dispatcher.UIThread));
    }
}
