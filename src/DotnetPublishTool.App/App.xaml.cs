using System.Windows;
using DotnetPublishTool.App.Services;
using DotnetPublishTool.App.ViewModels;

namespace DotnetPublishTool.App;

public partial class App : Application
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetLogger("App");

    /// <summary>日志文件写不进去时的原因（正常为 null）；由界面启动时显示到日志面板。</summary>
    public static string? LogProblem { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 日志先就位：界面日志一关就没了，崩溃原因、操作轨迹必须落盘（NLog.config 决定写哪、写多细）。
        HookUnhandledExceptions();
        Log.Info("工具启动：" + DescribeVersion() + "；参数：" + string.Join(' ', e.Args));

        // NLog 默认静默失败：日志目录写不进去（比如被沙箱打了 Low 标签、或目录权限异常）时，
        // 主动探一次并留给界面显示，免得"以为有日志、其实什么都没写"。
        LogProblem = ProbeLogDirectory();
        if (LogProblem is not null)
        {
            Log.Warn(LogProblem);
        }

        var headless = e.Args.Contains("--selftest", StringComparer.OrdinalIgnoreCase)
            || e.Args.Contains("--render", StringComparer.OrdinalIgnoreCase);

        if (headless)
        {
            var exitCode = SelfTest.Run(e.Args);
            Log.Info("自检结束，退出码：" + exitCode);
            Shutdown(exitCode);
            return;
        }

        var viewModel = AppServices.CreateMainViewModel(new WpfDialogService());
        var window = new MainWindow { DataContext = viewModel };
        MainWindow = window;
        window.Show();
    }

    /// <summary>未处理异常也写进日志：只记录、不拦截，程序行为与以前一致。</summary>
    private void HookUnhandledExceptions()
    {
        DispatcherUnhandledException += (_, args) => Log.Error(args.Exception, "界面线程未处理异常");
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error(args.ExceptionObject as Exception, "后台线程未处理异常");
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "未观察的任务异常");
            args.SetObserved();
        };
    }

    /// <summary>探一次日志目录：写不进文件就返回原因，能写返回 null。</summary>
    private static string? ProbeLogDirectory()
    {
        var target = (NLog.LogManager.Configuration?.AllTargets ?? [])
            .OfType<NLog.Targets.FileTarget>()
            .FirstOrDefault();
        if (target is null)
        {
            return "没有可用的日志目标：NLog.config 缺失或无效（工具日志将不会落盘）。";
        }

        var path = target.FileName?.Render(NLog.LogEventInfo.CreateNullEvent());
        if (string.IsNullOrWhiteSpace(path))
        {
            return "日志目标没有解析出文件路径（工具日志将不会落盘）。";
        }

        try
        {
            var directory = System.IO.Path.GetDirectoryName(path)!;
            System.IO.Directory.CreateDirectory(directory);
            var probe = System.IO.Path.Combine(directory, ".write-probe.tmp");
            System.IO.File.WriteAllText(probe, "ok");
            System.IO.File.Delete(probe);
            return null;
        }
        catch (Exception ex)
        {
            return "日志目录写不进去：" + path + "（" + ex.Message + "）。";
        }
    }

    private static string DescribeVersion()
    {
        var assembly = typeof(App).Assembly;
        return (assembly
                    .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                    .FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute)?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "未知";
    }
}
