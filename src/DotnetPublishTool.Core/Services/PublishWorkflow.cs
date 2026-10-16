using System.Diagnostics;
using System.Text;
using DotnetPublishTool.Core.Models;

namespace DotnetPublishTool.Core.Services;

/// <summary>
/// 发布编排：把（可能被修改过的）发布配置写到默认目录的副本，然后调用 dotnet publish，
/// 成功后按需打包 zip。全程不改动源项目里的任何文件。
/// </summary>
public sealed class PublishWorkflow
{
    private readonly PublishProfileStore _profileStore;
    private readonly PublishCommandBuilder _commandBuilder;
    private readonly DotnetProcessRunner _runner;
    private readonly ZipPackager _zipPackager;
    private readonly MsiPackageBuilder _msiBuilder;
    private readonly MsiVersionService _versionService;

    public PublishWorkflow(
        PublishProfileStore profileStore,
        PublishCommandBuilder commandBuilder,
        DotnetProcessRunner runner,
        ZipPackager zipPackager,
        MsiPackageBuilder msiBuilder,
        MsiVersionService versionService)
    {
        _profileStore = profileStore;
        _commandBuilder = commandBuilder;
        _runner = runner;
        _zipPackager = zipPackager;
        _msiBuilder = msiBuilder;
        _versionService = versionService;
    }

    /// <summary>只把修改后的配置副本写到默认目录，不执行发布。</summary>
    public SavedProfile SaveProfile(PublishRequest request)
    {
        var outputRoot = ResolveOutputRoot(request.OutputRoot);
        var publishDirectory = PathLayout.GetPublishDirectory(outputRoot, request.Project.Name);
        var profileDirectory = PathLayout.GetProfileDirectory(outputRoot, request.Project.Name);

        // 自动递增要在写配置副本之前解析：新版本会一路带到产物、zip 与安装包上。
        ApplyAutoVersionBump(request);
        var properties = BuildEffectiveProperties(request, publishDirectory);

        string profilePath;
        try
        {
            profilePath = _profileStore.Save(profileDirectory, request.ProfileName, properties, request.SourceItemGroupsXml);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            throw new PublishException(
                "无法写入发布配置副本：" + profileDirectory + Environment.NewLine
                + "原因：" + OutputDirectoryService.Describe(ex) + Environment.NewLine
                + "请把「输出根目录」改成一个可写的目录后重试。",
                ex);
        }

        return new SavedProfile(profilePath, publishDirectory);
    }

    /// <summary>
    /// 「打包 MSI 时自动递增版本」：把本次要用的版本换成递增后的版本。
    /// 这里只改本次请求（配置副本、产物、zip、MSI 都用新版本），写回源项目由发布成功后按选项再做。
    /// </summary>
    private void ApplyAutoVersionBump(PublishRequest request)
    {
        if (request.Msi is not { Enabled: true, AutoBumpVersion: true } msi)
        {
            return;
        }

        // 基准优先级：MSI 显式指定的安装版本 → 界面/命令行的版本覆盖 → 项目评估出的版本。
        var effectiveVersion = msi.ProductVersion ?? request.Options.VersionOverride ?? request.Project.Version;
        var plan = _versionService.Resolve(msi, request.Project.Path, effectiveVersion);
        if (plan is null)
        {
            return;
        }

        // 一次发布只用一个版本号：产物、zip 名、安装包、写回项目的都是它。
        msi.BaseVersion = plan.Value.BaseVersion;
        msi.ProductVersion = plan.Value.NextVersion;
        msi.Version = plan.Value.NextVersion;
        request.Options.VersionOverride = plan.Value.NextVersion;
    }

    public async Task<PublishResult> RunAsync(
        PublishRequest request,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        if (!request.Project.IsSdkStyle)
        {
            return new PublishResult
            {
                Success = false,
                ExitCode = -1,
                ErrorMessage = "该项目不是 SDK 风格项目，dotnet publish 无法发布。请改用 Visual Studio 的传统发布方式。",
            };
        }

        var stopwatch = Stopwatch.StartNew();
        var saved = SaveProfile(request);

        log?.Report("[配置] 已把本次生效的发布配置写入：" + saved.ProfilePath);
        log?.Report("[输出] 发布目录：" + saved.PublishDirectory);

        // 勾了生成 MSI、却没指定运行时标识符：发布变成「与平台无关」，NuGet 包里各平台的原生库
        // （runtimes/**、*.so / *.dylib / *.aar / *.xcframework.zip）会被整套抄进产物，安装包体积翻数倍。
        // 2026-10-16 实测：没填 RID 时发布目录 86 个文件 / 370 MB，MSI 212 MB；填 win-x64 后是 60 MB。
        if (request.Msi is { Enabled: true } && string.IsNullOrWhiteSpace(request.Options.RuntimeIdentifier))
        {
            log?.Report("[提醒] 勾了生成 MSI，但「运行时标识符」是空的：这次的发布会与平台无关，NuGet 包里各平台的原生库"
                + "（runtimes/**、*.so、*.dylib、*.aar、*.xcframework.zip）都可能被抄进发布产物 —— 安装包会大好几倍。"
                + "建议到「常用设置 → 运行时标识符」填 win-x64 再打包。");
        }

        var protocol = request.ExtraProperties
            .FirstOrDefault(p => string.Equals(p.Key, "PublishProtocol", StringComparison.OrdinalIgnoreCase))?.Value;
        if (!string.IsNullOrWhiteSpace(protocol) && !protocol.Equals("FileSystem", StringComparison.OrdinalIgnoreCase))
        {
            log?.Report($"[提示] 该配置的 PublishProtocol={protocol}，本工具统一按文件夹（FileSystem）方式发布。");
        }

        try
        {
            if (request.Options.CleanPublishDirectory && Directory.Exists(saved.PublishDirectory))
            {
                log?.Report("[清理] 删除已有发布目录…");
                Directory.Delete(saved.PublishDirectory, recursive: true);
            }

            Directory.CreateDirectory(saved.PublishDirectory);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return new PublishResult
            {
                Success = false,
                ExitCode = -1,
                PublishDirectory = saved.PublishDirectory,
                ProfilePath = saved.ProfilePath,
                ErrorMessage = "无法准备发布目录：" + saved.PublishDirectory + Environment.NewLine
                    + "原因：" + OutputDirectoryService.Describe(ex),
            };
        }

        var arguments = _commandBuilder.Build(request, saved.ProfilePath, saved.PublishDirectory);
        var commandLine = PublishCommandBuilder.ToDisplayString(arguments);
        log?.Report("[命令] " + commandLine);

        var processResult = await _runner
            .RunAsync(arguments, request.Project.Directory, log, cancellationToken)
            .ConfigureAwait(false);

        stopwatch.Stop();

        if (processResult.ExitCode != 0)
        {
            return new PublishResult
            {
                Success = false,
                ExitCode = processResult.ExitCode,
                PublishDirectory = saved.PublishDirectory,
                ProfilePath = saved.ProfilePath,
                CommandLine = commandLine,
                Duration = stopwatch.Elapsed,
                ErrorMessage = BuildErrorText(processResult),
            };
        }

        var fileCount = CountFiles(saved.PublishDirectory);
        log?.Report($"[完成] 发布成功，共 {fileCount} 个文件，用时 {stopwatch.Elapsed.TotalSeconds:F1}s。");

        string? zipPath = null;
        if (request.Zip is { Enabled: true })
        {
            var zip = request.Zip;
            zip.Version = request.Options.VersionOverride ?? request.Project.Version;
            var zipName = ZipNaming.Resolve(zip, request.Project.Name);
            var zipDirectory = string.IsNullOrWhiteSpace(zip.OutputDirectory)
                ? PathLayout.GetProjectRoot(ResolveOutputRoot(request.OutputRoot), request.Project.Name)
                : Path.GetFullPath(zip.OutputDirectory!);
            zipPath = Path.Combine(zipDirectory, zipName);

            log?.Report("[打包] 正在生成压缩包：" + zipPath);
            _zipPackager.Create(saved.PublishDirectory, zipPath, zip.IncludeRootFolder, request.Project.Name);
            log?.Report("[打包] 完成：" + zipPath);
        }

        // 安装包失败不回滚已经成功的发布：把原因带回去，由界面提示「发布成功但没生成 MSI」。
        string? msiPath = null;
        string? msiError = null;
        if (request.Msi is { Enabled: true })
        {
            var msi = request.Msi;
            msi.Version = request.Options.VersionOverride ?? request.Project.Version;
            var msiDirectory = string.IsNullOrWhiteSpace(msi.OutputDirectory)
                ? PathLayout.GetProjectRoot(ResolveOutputRoot(request.OutputRoot), request.Project.Name)
                : Path.GetFullPath(msi.OutputDirectory!);

            var productName = string.IsNullOrWhiteSpace(msi.ProductName) ? request.Project.Name : msi.ProductName!.Trim();

            // 版本号没变、又往同一个目录打：会覆盖同名包，而且用户没法从版本号区分两次发布 —— 提醒一次。
            // 同版本重装本身从 1.2.1 起是能装上的（新产品码 + 升级范围含本版本），所以这里只是建议而非错误。
            // 版本号口径必须与生成器一致：生成器用的是 ProductVersion（没给才回落到项目版本），
            // 拿 msi.Version 算文件名会算错，这里专门对齐（2026-10-14 实测踩到）。
            var effectiveMsiVersion = string.IsNullOrWhiteSpace(msi.ProductVersion) ? msi.Version : msi.ProductVersion!.Trim();
            var targetMsiPath = Path.Combine(
                msiDirectory,
                ZipNaming.Resolve(msi.NamingMode, msi.DateFormat, msi.CustomName, effectiveMsiVersion, productName, ".msi"));
            var previousVersion = _versionService.LastRecordedVersion(request.Project.Path);
            var sameNameExists = File.Exists(targetMsiPath);
            var sameAsRecorded = !string.IsNullOrWhiteSpace(previousVersion)
                && string.Equals(previousVersion, effectiveMsiVersion, StringComparison.OrdinalIgnoreCase);
            if (sameNameExists || sameAsRecorded)
            {
                var reason = sameNameExists ? "输出目录里已有同名安装包，会被覆盖" : "和本机记录的上一版相同";
                log?.Report($"[MSI][提醒] 这次打的版本号还是 {effectiveMsiVersion}（{reason}）：同版本重装能装上（会先卸旧再装新），"
                    + "但发出去的包最好每次递增版本号 —— 否则用户没法从版本号区分两次发布。");
            }
            log?.Report($"[MSI] 正在生成安装包（{productName}，安装到 Program Files）…");
            try
            {
                var built = _msiBuilder.Create(
                    saved.PublishDirectory, msiDirectory, msi, request.Project.Name, log, cancellationToken);
                msiPath = built.MsiPath;
                foreach (var warning in built.Warnings)
                {
                    log?.Report("[MSI] " + warning);
                }

                log?.Report($"[MSI] 完成：{msiPath}（{built.FileCount} 个文件，版本 {built.ProductVersion}）");
            }
            catch (PublishException ex)
            {
                msiError = ex.Message;
                log?.Report("[MSI] 生成失败：" + ex.Message);
            }
            catch (Exception ex)
            {
                msiError = ex.Message;
                log?.Report("[MSI] 生成失败：" + ex);
            }
        }

        // 安装包真的产出了才推进版本：MSI 失败时版本不变，重跑还是同一个版本，不会白吃掉一个号。
        string? bumpedVersion = null;
        string? versionError = null;
        if (msiPath is not null && request.Msi is { Enabled: true, AutoBumpVersion: true } msiPlan
            && !string.IsNullOrWhiteSpace(msiPlan.Version))
        {
            bumpedVersion = msiPlan.Version;
            log?.Report($"[版本] 自动递增：{msiPlan.BaseVersion ?? "（未知）"} → {bumpedVersion}");
            try
            {
                var backupDirectory = Path.Combine(
                    PathLayout.GetProjectRoot(ResolveOutputRoot(request.OutputRoot), request.Project.Name),
                    "csproj-backup");
                _versionService.Apply(
                    request.Project.Path, bumpedVersion!, msiPlan.BaseVersion, msiPlan.WriteVersionBack, backupDirectory, log);
            }
            catch (Exception ex)
            {
                versionError = ex.Message;
                log?.Report("[版本] 写回/记录失败：" + ex.Message);
            }
        }

        return new PublishResult
        {
            Success = true,
            ExitCode = 0,
            PublishDirectory = saved.PublishDirectory,
            ProfilePath = saved.ProfilePath,
            ZipPath = zipPath,
            MsiPath = msiPath,
            MsiError = msiError,
            BumpedVersion = bumpedVersion,
            VersionError = versionError,
            CommandLine = commandLine,
            Duration = stopwatch.Elapsed,
            FileCount = fileCount,
        };
    }

    /// <summary>合并「常用设置」与「其他属性」，得到最终写入 .pubxml 的属性集合。</summary>
    public static List<ProfileProperty> BuildEffectiveProperties(PublishRequest request, string publishDirectory)
    {
        var ordered = new List<ProfileProperty>();

        void Set(string key, string? value)
        {
            var existing = ordered.FindIndex(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                ordered[existing] = new ProfileProperty(key, value);
            }
            else
            {
                ordered.Add(new ProfileProperty(key, value));
            }
        }

        // 先放原配置保留下来的其他属性（Platform、PublishProtocol、_TargetId 等）。
        foreach (var property in request.ExtraProperties)
        {
            if (!string.IsNullOrWhiteSpace(property.Key))
            {
                Set(property.Key.Trim(), property.Value);
            }
        }

        // Web SDK 的文件夹发布需要这两个标记；源配置已有同键时保留其值，不覆盖。
        if (!ordered.Any(p => string.Equals(p.Key, "PublishProtocol", StringComparison.OrdinalIgnoreCase)))
        {
            ordered.Add(new ProfileProperty("PublishProtocol", "FileSystem"));
        }

        if (!ordered.Any(p => string.Equals(p.Key, "_TargetId", StringComparison.OrdinalIgnoreCase)))
        {
            ordered.Add(new ProfileProperty("_TargetId", "Folder"));
        }

        var options = request.Options;
        Set("Configuration", options.Configuration);
        Set("PublishDir", publishDirectory);

        if (!string.IsNullOrWhiteSpace(options.TargetFramework))
        {
            Set("TargetFramework", options.TargetFramework);
        }

        if (!string.IsNullOrWhiteSpace(options.RuntimeIdentifier))
        {
            Set("RuntimeIdentifier", options.RuntimeIdentifier);
        }
        else
        {
            ordered.RemoveAll(p => string.Equals(p.Key, "RuntimeIdentifier", StringComparison.OrdinalIgnoreCase));
        }

        Set("SelfContained", Bool(options.SelfContained));
        Set("PublishSingleFile", Bool(options.PublishSingleFile));
        Set("PublishReadyToRun", Bool(options.PublishReadyToRun));
        Set("PublishTrimmed", Bool(options.PublishTrimmed));

        if (!string.IsNullOrWhiteSpace(options.EnvironmentName))
        {
            Set("EnvironmentName", options.EnvironmentName);
        }

        if (!string.IsNullOrWhiteSpace(options.VersionOverride))
        {
            // 与命令行一致：四个版本键一起写，避免被项目里显式的版本键压过去。
            foreach (var key in VersionKeys.All)
            {
                Set(key, options.VersionOverride);
            }
        }

        return PublishOptionKeys.Order(ordered).ToList();
    }

    /// <summary>把属性集合渲染成 .pubxml 文本，供界面预览。</summary>
    public static string RenderProfileXml(IEnumerable<ProfileProperty> properties)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        builder.AppendLine("<Project>");
        builder.AppendLine("  <PropertyGroup>");
        foreach (var property in PublishOptionKeys.Order(properties))
        {
            if (string.IsNullOrWhiteSpace(property.Key))
            {
                continue;
            }

            builder.Append("    <").Append(property.Key.Trim()).Append('>');
            builder.Append(Escape(property.Value));
            builder.Append("</").Append(property.Key.Trim()).AppendLine(">");
        }

        builder.AppendLine("  </PropertyGroup>");
        builder.AppendLine("</Project>");
        return builder.ToString();
    }

    private static string Escape(string? value) =>
        (value ?? string.Empty)
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");

    private static string Bool(bool value) => value ? "true" : "false";

    /// <summary>
    /// 输出根目录一律规整为绝对路径：dotnet publish 的工作目录是项目目录，
    /// 相对路径会被解析到项目目录下，导致产物落到意料之外的位置。
    /// </summary>
    private static string ResolveOutputRoot(string outputRoot) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(outputRoot) ? PathLayout.DefaultOutputRoot : outputRoot);

    private static int CountFiles(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Count();
        }
        catch
        {
            return 0;
        }
    }

    private static string BuildErrorText(ProcessResult result)
    {
        var text = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        var lines = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .TakeLast(8)
            .ToArray();

        return lines.Length == 0 ? $"dotnet publish 退出码 {result.ExitCode}。" : string.Join(Environment.NewLine, lines);
    }
}
