using System.Diagnostics;
using System.IO;
using DotnetPublishTool.Core.Models;

namespace DotnetPublishTool.Core.Services;

/// <summary>
/// 前端构建编排：在项目目录执行包管理器的构建命令，再把 dist 目录打成以日期区分的 zip。
/// 只读取 dist 并写压缩包，不修改前端源码；依赖需要事先安装（本工具不执行 install）。
/// </summary>
public sealed class FrontendBuildWorkflow
{
    private readonly ProcessRunner _runner;
    private readonly ZipPackager _zipPackager;

    public FrontendBuildWorkflow(ProcessRunner runner, ZipPackager zipPackager)
    {
        _runner = runner;
        _zipPackager = zipPackager;
    }

    public async Task<FrontendBuildResult> RunAsync(
        FrontendBuildRequest request,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var options = request.Options;
        var commandLine = BuildCommandLine(options);
        var projectDirectory = request.ProjectDirectory;

        if (string.IsNullOrWhiteSpace(projectDirectory) || !Directory.Exists(projectDirectory))
        {
            return Fail(commandLine, stopwatch, "前端项目目录不存在：" + projectDirectory);
        }

        var projectRoot = Path.GetFullPath(projectDirectory);
        if (!File.Exists(Path.Combine(projectRoot, FrontendProjectReader.PackageFileName)))
        {
            return Fail(commandLine, stopwatch, "项目目录里没有 package.json：" + projectRoot);
        }

        log?.Report("[前端] 项目目录：" + projectRoot);

        string distDirectory;
        try
        {
            distDirectory = ResolveDistDirectory(projectRoot, options.DistDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Fail(commandLine, stopwatch, "构建输出目录无效：" + ex.Message);
        }

        log?.Report("[前端] dist 目录：" + distDirectory);

        if (request.Build)
        {
            log?.Report("[命令] " + commandLine + "（工作目录：" + projectRoot + "）");
            ProcessResult processResult;
            try
            {
                processResult = await _runner.RunShellAsync(commandLine, projectRoot, log, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Fail(commandLine, stopwatch, "无法启动构建命令：" + ex.Message, distDirectory);
            }

            if (!processResult.Succeeded)
            {
                log?.Report("[错误] 构建失败，退出码 " + processResult.ExitCode + "。");
                return Fail(commandLine, stopwatch, BuildErrorText(processResult), distDirectory);
            }

            log?.Report("[完成] 构建成功。");
        }
        else
        {
            log?.Report("[跳过] 不执行构建，直接打包现有 dist。");
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(distDirectory))
        {
            return Fail(
                commandLine,
                stopwatch,
                "dist 目录不存在：" + distDirectory + "。请先执行构建，或修改「构建输出目录」。",
                distDirectory);
        }

        var fileCount = Directory.EnumerateFiles(distDirectory, "*", SearchOption.AllDirectories).Count();
        if (fileCount == 0)
        {
            return Fail(commandLine, stopwatch, "dist 目录里没有文件：" + distDirectory, distDirectory);
        }

        string zipPath;
        try
        {
            var zip = new ZipOptions
            {
                NamingMode = options.NamingMode,
                DateFormat = string.IsNullOrWhiteSpace(options.DateFormat) ? ZipOptions.DefaultDateFormat : options.DateFormat,
                CustomName = options.CustomName,
                IncludeRootFolder = options.IncludeRootFolder,
            };
            var zipName = ZipNaming.Resolve(zip, request.ProjectName);
            var zipDirectory = string.IsNullOrWhiteSpace(options.OutputDirectory)
                ? PathLayout.GetProjectRoot(ResolveOutputRoot(request.OutputRoot), request.ProjectName)
                : Path.GetFullPath(options.OutputDirectory!);
            zipPath = MakeUniquePath(Path.Combine(zipDirectory, zipName));
        }
        catch (FormatException)
        {
            return Fail(commandLine, stopwatch, "日期格式无效，请检查「日期格式」设置。", distDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Fail(commandLine, stopwatch, "压缩包路径无效：" + ex.Message, distDirectory);
        }

        log?.Report("[打包] 正在生成压缩包：" + zipPath);
        try
        {
            _zipPackager.Create(distDirectory, zipPath, options.IncludeRootFolder, request.ProjectName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return Fail(commandLine, stopwatch, "生成压缩包失败：" + ex.Message, distDirectory);
        }

        stopwatch.Stop();
        log?.Report($"[完成] 前端打包成功，共 {fileCount} 个文件，用时 {stopwatch.Elapsed.TotalSeconds:F1}s。");

        return new FrontendBuildResult
        {
            Success = true,
            ExitCode = 0,
            CommandLine = commandLine,
            DistDirectory = distDirectory,
            ZipPath = zipPath,
            FileCount = fileCount,
            Duration = stopwatch.Elapsed,
        };
    }

    /// <summary>按包管理器与构建参数拼出命令行；构建参数为空时只返回包管理器。</summary>
    public static string BuildCommandLine(FrontendPackageOptions options)
    {
        var manager = options.PackageManager switch
        {
            FrontendPackageManager.Npm => "npm",
            FrontendPackageManager.Yarn => "yarn",
            _ => "pnpm",
        };

        var arguments = (options.BuildArguments ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(arguments) ? manager : manager + " " + arguments;
    }

    /// <summary>把 dist 目录解析为绝对路径；相对路径按项目目录解析。</summary>
    public static string ResolveDistDirectory(string projectDirectory, string? distDirectory)
    {
        var value = string.IsNullOrWhiteSpace(distDirectory)
            ? FrontendPackageOptions.DefaultDistDirectory
            : distDirectory.Trim();
        return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(projectDirectory, value));
    }

    private static string ResolveOutputRoot(string? outputRoot)
    {
        return Path.GetFullPath(string.IsNullOrWhiteSpace(outputRoot) ? PathLayout.DefaultOutputRoot : outputRoot);
    }

    /// <summary>同名压缩包已存在时追加 _2、_3…，避免同一天多次打包互相覆盖。</summary>
    private static string MakeUniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var index = 2; index < 1000; index++)
        {
            var candidate = Path.Combine(directory, name + "_" + index + extension);
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, name + "_" + DateTime.Now.ToString("HHmmssfff") + extension);
    }

    private static string BuildErrorText(ProcessResult result)
    {
        var text = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tail = lines.Length <= 10 ? lines : lines[^10..];
        var detail = tail.Length == 0
            ? string.Empty
            : Environment.NewLine + string.Join(Environment.NewLine, tail);
        return "构建失败，退出码 " + result.ExitCode + "。" + detail;
    }

    private static FrontendBuildResult Fail(
        string commandLine,
        Stopwatch stopwatch,
        string message,
        string? distDirectory = null)
    {
        stopwatch.Stop();
        return new FrontendBuildResult
        {
            Success = false,
            ExitCode = -1,
            CommandLine = commandLine,
            DistDirectory = distDirectory,
            Duration = stopwatch.Elapsed,
            ErrorMessage = message,
        };
    }
}
