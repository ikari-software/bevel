using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Bevel.App;

public partial class App : Application
{
    /// <summary>
    /// The composition-root service provider, assigned in <see cref="Program.Main"/>
    /// before Avalonia starts. Null in the (headless-test) case where no host is built.
    /// </summary>
    public static IServiceProvider? Services { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
