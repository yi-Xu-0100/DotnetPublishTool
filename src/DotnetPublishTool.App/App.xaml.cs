using System.Windows;
using DotnetPublishTool.App.Services;
using DotnetPublishTool.App.ViewModels;

namespace DotnetPublishTool.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var headless = e.Args.Contains("--selftest", StringComparer.OrdinalIgnoreCase)
            || e.Args.Contains("--render", StringComparer.OrdinalIgnoreCase);

        if (headless)
        {
            Shutdown(SelfTest.Run(e.Args));
            return;
        }

        var viewModel = AppServices.CreateMainViewModel(new WpfDialogService());
        var window = new MainWindow { DataContext = viewModel };
        MainWindow = window;
        window.Show();
    }
}
