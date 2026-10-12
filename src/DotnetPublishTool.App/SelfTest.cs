using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DotnetPublishTool.App.Services;
using DotnetPublishTool.App.ViewModels;
using DotnetPublishTool.Core.Models;
using DotnetPublishTool.Core.Services;

namespace DotnetPublishTool.App;

/// <summary>
/// 无界面自检：加载真实 XAML（验证资源与控件），再驱动 ViewModel 完成一次真实发布（可选打包）。
/// 用法：DotnetPublishTool.exe --selftest --project &lt;项目&gt; --out &lt;输出目录&gt; [--zip] [--msi] [--zip-mode ProjectNameVersion]
/// </summary>
internal static class SelfTest
{
    public static int Run(string[] args)
    {
        var project = GetOption(args, "--project");
        var outputRoot = GetOption(args, "--out");
        var zipMode = GetOption(args, "--zip-mode");
        var renderPath = GetOption(args, "--render");
        var wantZip = args.Contains("--zip", StringComparer.OrdinalIgnoreCase);
        var wantMsi = args.Contains("--msi", StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(project))
        {
            Console.Error.WriteLine("缺少 --project 参数。");
            return 2;
        }

        try
        {
            // 只渲染界面（不发布）：用于目视核对布局是否有控件没显示出来。
            if (!string.IsNullOrWhiteSpace(renderPath))
            {
                // 同样要清空同步上下文，否则 GetResult() 会与 Dispatcher 互相等待。
                var renderPrevious = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(null);
                try
                {
                    var renderDialogs = new SilentDialogService();
                    var renderSettings = new AppSettingsService(
                        Path.Combine(outputRoot ?? Path.GetTempPath(), "selftest-settings.json"));
                    var renderViewModel = AppServices.CreateMainViewModel(renderDialogs, renderSettings);
                    if (!string.IsNullOrWhiteSpace(outputRoot))
                    {
                        renderViewModel.OutputRoot = outputRoot!;
                    }

                    if (!string.IsNullOrWhiteSpace(project))
                    {
                        renderViewModel.LoadPathAsync(project!).GetAwaiter().GetResult();
                    }

                    RenderToPng(renderViewModel, renderPath!, 1240, 880);
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(renderPrevious);
                }

                Console.WriteLine("rendered: " + renderPath);
                return 0;
            }

            // 1) 构造窗口以真实解析 XAML（StaticResource 缺失等问题会在此抛出）。
            var probe = new MainWindow();
            GC.KeepAlive(probe);

            // 2) 清除同步上下文，避免阻塞 UI 线程时与 Dispatcher 互相等待。
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                var dialogs = new SilentDialogService();
                var settingsPath = Path.Combine(outputRoot ?? Path.GetTempPath(), "selftest-settings.json");
                var viewModel = AppServices.CreateMainViewModel(dialogs, new AppSettingsService(settingsPath));

                if (!string.IsNullOrWhiteSpace(outputRoot))
                {
                    viewModel.OutputRoot = outputRoot!;
                }

                viewModel.LoadPathAsync(project!).GetAwaiter().GetResult();

                var profileName = GetOption(args, "--profile");
                if (!string.IsNullOrWhiteSpace(profileName))
                {
                    var match = viewModel.Profiles.FirstOrDefault(
                        p => string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase));
                    if (match is not null)
                    {
                        viewModel.SelectedProfile = match;
                    }
                }

                if (!string.IsNullOrWhiteSpace(zipMode) && Enum.TryParse<ZipNamingMode>(zipMode, ignoreCase: true, out var parsed))
                {
                    viewModel.ZipNamingMode = parsed;
                }

                viewModel.MakeZip = wantZip;
                viewModel.MakeMsi = wantMsi;

                var result = viewModel.PublishAsync().GetAwaiter().GetResult();
                var payload = JsonSerializer.Serialize(new
                {
                    success = result?.Success ?? false,
                    exitCode = result?.ExitCode ?? -1,
                    publishDirectory = result?.PublishDirectory,
                    profilePath = result?.ProfilePath,
                    zipPath = result?.ZipPath,
                    msiPath = result?.MsiPath,
                    msiError = result?.MsiError,
                    fileCount = result?.FileCount ?? 0,
                    durationSeconds = Math.Round(result?.Duration.TotalSeconds ?? 0, 2),
                    error = result?.ErrorMessage,
                    dialogErrors = dialogs.Errors,
                    dialogInfos = dialogs.Infos,
                    integrity = ProcessIntegrity.Describe(ProcessIntegrity.GetCurrent()),
                    integrityTooLow = ProcessIntegrity.IsBelowMedium,
                    status = viewModel.StatusText,
                    recentProjects = viewModel.RecentProjects.Select(r => r.DisplayName).ToArray(),
                }, new JsonSerializerOptions { WriteIndented = true });

                // 结果写到「实际生效」的输出目录：输出根目录可能已被自动切换到可写位置。
                WriteResult(viewModel.OutputRoot, payload);
                Console.WriteLine(payload);
                return result?.Success == true ? 0 : 1;
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        }
        catch (Exception ex)
        {
            WriteResult(outputRoot, ex.ToString());
            Console.Error.WriteLine(ex);
            return 2;
        }
    }

    /// <summary>把主界面内容离屏渲染成 PNG，用于目视核对。</summary>
    private static void RenderToPng(MainViewModel viewModel, string path, int width, int height)
    {
        var window = new MainWindow { DataContext = viewModel };
        if (window.Content is not FrameworkElement root)
        {
            throw new InvalidOperationException("窗口没有可渲染的内容。");
        }

        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void WriteResult(string? outputRoot, string payload)
    {
        try
        {
            var target = string.IsNullOrWhiteSpace(outputRoot) ? Path.GetTempPath() : outputRoot!;
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "selftest-result.txt"), payload);
        }
        catch
        {
            // 自检结果写文件失败不影响退出码。
        }
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
