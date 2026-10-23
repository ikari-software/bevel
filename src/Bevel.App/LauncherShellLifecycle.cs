using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Bevel.App.Supervision;
using Bevel.Interop;

namespace Bevel.App;

/// <summary>
/// The automation model's <c>quit</c> verb (<see cref="IShellLifecycle"/>), wired to the same teardown
/// Start ▸ Turn Off drives.
///
/// <para>Quitting has to fan out to the LAUNCHER, not just end the process that received the request:
/// the launcher owns core, taskbar and the filers, and its shutdown is what restores the desktop the
/// shell borrowed — on Windows the native taskbar and its auto-hide state (bevel-h0sr), on macOS the
/// Dock. Ending only this process would strand the siblings and leave those settings as Bevel left
/// them. That is also why killing the shell is the wrong way to stop it, and why this verb exists:
/// before it, a clean shutdown was reachable only by clicking.</para>
///
/// <para>Unsupervised (no launcher env — a bare <c>--role=taskbar</c> dev run) the send reports false
/// and this falls back to shutting down the local Avalonia app, which is the best "whole shell" a
/// lone process can offer.</para>
/// </summary>
internal sealed class LauncherShellLifecycle : IShellLifecycle
{
    public Task QuitAsync(CancellationToken ct)
    {
        // Deliberately not awaited-through: the caller is answering over a socket the shell is about to
        // close, so this returns on ACCEPTANCE. TrySend is itself bounded and never throws.
        if (LauncherControl.TrySend(LauncherControl.Command.Quit))
            return Task.CompletedTask;

        // No launcher: end this process's UI loop. Posted, so the automation reply is written before
        // the lifetime tears the dispatcher down underneath it.
        Dispatcher.UIThread.Post(() =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        });
        return Task.CompletedTask;
    }
}
