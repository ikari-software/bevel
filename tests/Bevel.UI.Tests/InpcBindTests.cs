using System.ComponentModel;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Bevel.UI;
using Xunit;

namespace Bevel.UI.Tests;

/// <summary>
/// bevel-gww.9: <see cref="InpcBind"/> is the reflection-free replacement for code-behind
/// <c>new Binding(...)</c> — apply runs on attach and on each matching PropertyChanged, and
/// detach unsubscribes so recycled rows never leak handlers onto a long-lived view-model.
/// </summary>
public sealed class InpcBindTests
{
    private sealed class Vm : INotifyPropertyChanged
    {
        private int _count;
        public int Count
        {
            get => _count;
            set { _count = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        public int HandlerCount => PropertyChanged?.GetInvocationList().Length ?? 0;
    }

    [AvaloniaFact]
    public void Apply_runs_on_attach_and_on_each_matching_change()
    {
        var vm = new Vm { Count = 1 };
        var host = new Border();
        var w = new Window { Content = host, Width = 100, Height = 100 };
        InpcBind.Attach(host, vm, nameof(Vm.Count), () => host.Tag = vm.Count);

        w.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, host.Tag);                 // applied on attach

        vm.Count = 5;
        Assert.Equal(5, host.Tag);                 // applied on the matching change
        w.Close();
    }

    [AvaloniaFact]
    public void Detach_unsubscribes()
    {
        var vm = new Vm();
        var host = new Border();
        var w = new Window { Content = host, Width = 100, Height = 100 };
        InpcBind.Attach(host, vm, nameof(Vm.Count), () => { });
        w.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, vm.HandlerCount);

        w.Content = null;                          // detaches host
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, vm.HandlerCount);          // no leak onto the view-model
        w.Close();
    }

    [AvaloniaFact]
    public void Other_property_names_are_ignored()
    {
        var vm = new Vm { Count = 1 };
        var host = new Border { Tag = -1 };
        var w = new Window { Content = host, Width = 100, Height = 100 };
        InpcBind.Attach(host, vm, nameof(Vm.Count), () => host.Tag = vm.Count);
        w.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, host.Tag);

        vm.Raise("SomethingElse");
        Assert.Equal(1, host.Tag);                 // unchanged
        vm.Count = 9;
        Assert.Equal(9, host.Tag);                 // matching name still applies
        w.Close();
    }
}
