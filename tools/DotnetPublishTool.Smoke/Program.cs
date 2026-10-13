using System.Text.Json;
using DotnetPublishTool.Core.Models;
using DotnetPublishTool.Core.Services;

const string Usage = """
DotnetPublishTool 端到端冒烟工具

  list     <project>                      解析项目并列出发现的发布配置
  publish  <project> [options]            按配置发布（可打包）
  pack-msi <发布目录> [options]            把已发布的目录做成 MSI 安装包
  bump     <版本> [<版本> ...]             只算「自动递增版本」的结果，用于核对规则

options:
  --out <dir>            输出目录（publish 默认 ./.smoke-out；pack-msi 默认当前目录）
  --profile <name>       指定发布配置名（默认第一个）
  --no-zip               不生成 zip
  --zip-mode <mode>      ProjectName | ProjectNameVersion | ProjectNameDate | Custom
  --zip-name <name>      Custom 模式使用的名称
  --configuration <cfg>  生成配置（默认取配置或 Release）
  --rid <rid>            运行时标识符
  --self-contained       自包含
  --single-file          单文件
  --no-clean             发布前不清空发布目录
  --msi                  发布成功后生成 MSI 安装包
  --msi-product <名>     MSI 产品名（默认项目名）
  --msi-manufacturer <名> MSI 发布者（默认产品名）
  --msi-version <x.y.z>  MSI 安装版本（默认项目版本）
  --msi-mode <mode>      MSI 命名方式：ProjectName | ProjectNameVersion | ProjectNameDate | Custom
  --msi-bump-version     打包前自动把版本递增一位（新版本用于本次发布）
  --msi-write-version-back
                         递增后把新版本写回项目 csproj（会改源文件；默认不写回，只记在本机版本记录里）
  --msi-name <name>      MSI 命名方式为 Custom 时使用的名称
  --no-msi-shortcut      不创建开始菜单快捷方式
  --no-msi-desktop-shortcut
                         不创建桌面快捷方式

list 也可只在命令行传入项目路径（等价于 list）。
""";

if (args.Length == 0)
{
    Console.WriteLine(Usage);
    return 2;
}

var rawCommand = args[0];
var command = rawCommand.ToLowerInvariant();
var rest = args.Skip(1).ToList();

if (command is "-h" or "--help" or "help")
{
    Console.WriteLine(Usage);
    return 0;
}

if (command.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
    || command.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)
    || command.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase))
{
    rest.Insert(0, rawCommand);
    command = "list";
}

return command switch
{
    "list" => await ListAsync(rest),
    "publish" => await PublishAsync(rest),
    "pack-msi" => PackMsi(rest),
    "bump" => Bump(rest),
    _ => Unknown(),
};

static int Unknown()
{
    Console.Error.WriteLine("未知命令。");
    return 2;
}

static async Task<int> ListAsync(List<string> arguments)
{
    var target = arguments.FirstOrDefault(a => !a.StartsWith("-"));
    if (string.IsNullOrWhiteSpace(target))
    {
        Console.Error.WriteLine("缺少项目路径。");
        return 2;
    }

    var runner = new DotnetProcessRunner();
    var inspector = new ProjectInspector(runner);
    var store = new PublishProfileStore();

    var project = await inspector.InspectAsync(target);
    var profiles = store.Discover(project);

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        project = new
        {
            project.Name,
            project.Path,
            project.Directory,
            kind = project.KindText,
            sdk = project.SdkName,
            frameworks = project.TargetFrameworks,
            project.Version,
            project.IsSdkStyle,
            project.EvaluationWarning,
        },
        profiles = profiles.Select(p => new
        {
            p.Name,
            p.SourcePath,
            p.IsSynthetic,
            propertyCount = p.Properties.Count,
            publishDir = p.GetValue("PublishDir"),
            runtimeIdentifier = p.GetValue("RuntimeIdentifier"),
            selfContained = p.GetValue("SelfContained"),
        }),
    }, new JsonSerializerOptions { WriteIndented = true }));

    return project.IsSdkStyle ? 0 : 1;
}

static async Task<int> PublishAsync(List<string> arguments)
{
    var target = arguments.FirstOrDefault(a => !a.StartsWith("-"));
    if (string.IsNullOrWhiteSpace(target))
    {
        Console.Error.WriteLine("缺少项目路径。");
        return 2;
    }

    var outputRoot = GetOption(arguments, "--out")
        ?? Path.Combine(Directory.GetCurrentDirectory(), ".smoke-out");
    var profileName = GetOption(arguments, "--profile");
    var zipModeText = GetOption(arguments, "--zip-mode");
    var zipName = GetOption(arguments, "--zip-name");
    var configuration = GetOption(arguments, "--configuration");
    var rid = GetOption(arguments, "--rid");

    var useZip = !HasFlag(arguments, "--no-zip");
    var useMsi = HasFlag(arguments, "--msi");
    var selfContained = HasFlag(arguments, "--self-contained");
    var singleFile = HasFlag(arguments, "--single-file");
    var clean = !HasFlag(arguments, "--no-clean");

    var runner = new DotnetProcessRunner();
    var inspector = new ProjectInspector(runner);
    var store = new PublishProfileStore();
    var workflow = new PublishWorkflow(
        store, new PublishCommandBuilder(), runner, new ZipPackager(), new MsiPackageBuilder(), new MsiVersionService());

    var project = await inspector.InspectAsync(target);
    var profiles = store.Discover(project);
    var profile = profiles.FirstOrDefault(p => string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase))
        ?? profiles[0];

    var zipNamingMode = Enum.TryParse<ZipNamingMode>(zipModeText, ignoreCase: true, out var parsedMode)
        ? parsedMode
        : ZipNamingMode.ProjectNameVersion;

    var options = new PublishOptions
    {
        Configuration = configuration ?? profile.GetValue("Configuration") ?? "Release",
        TargetFramework = profile.GetValue("TargetFramework") ?? project.TargetFrameworks.FirstOrDefault(),
        RuntimeIdentifier = HasFlag(arguments, "--use-profile-rid") ? profile.GetValue("RuntimeIdentifier") : (rid ?? profile.GetValue("RuntimeIdentifier")),
        SelfContained = selfContained || (profile.GetBool("SelfContained") ?? false) && !HasFlag(arguments, "--framework-dependent"),
        PublishSingleFile = singleFile || (profile.GetBool("PublishSingleFile") ?? false),
        PublishReadyToRun = profile.GetBool("PublishReadyToRun") ?? false,
        PublishTrimmed = profile.GetBool("PublishTrimmed") ?? false,
        CleanPublishDirectory = clean,
    };

    var request = new PublishRequest
    {
        Project = project,
        Options = options,
        OutputRoot = outputRoot,
        ProfileName = profile.Name,
        ExtraProperties = profile.Properties
            .Where(p => !PublishOptionKeys.IsCurated(p.Key))
            .Select(p => p.Clone())
            .ToList(),
        SourceItemGroupsXml = profile.ItemGroupsXml,
        Zip = new ZipOptions
        {
            Enabled = useZip,
            NamingMode = zipNamingMode,
            // 与应用默认一致：默认不包含顶层文件夹；加 --zip-root-folder 才包含。
            IncludeRootFolder = HasFlag(arguments, "--zip-root-folder"),
            CustomName = zipName,
            Version = project.Version,
        },
        Msi = useMsi
            ? BuildMsiOptions(arguments, project.Version)
            : null,
    };

    var captured = new List<string>();
    var captureLock = new object();
    var log = new Progress<string>(line =>
    {
        lock (captureLock)
        {
            captured.Add(line);
        }

        Console.WriteLine("[dotnet] " + line);
    });
    PublishResult result;
    try
    {
        result = await workflow.RunAsync(request, log, CancellationToken.None);
    }
    catch (PublishException ex)
    {
        // 可预期的失败（例如输出目录不可写）：给出面向用户的消息，而不是未处理异常。
        Console.Error.WriteLine(ex.Message);
        return 1;
    }

    try
    {
        Directory.CreateDirectory(outputRoot);
        File.WriteAllLines(Path.Combine(outputRoot, "dotnet-log.txt"), captured);
    }
    catch
    {
        // 仅用于验证日志编码，写失败不影响发布结果。
    }

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        result.Success,
        result.ExitCode,
        result.PublishDirectory,
        result.ProfilePath,
        result.ZipPath,
        result.MsiPath,
        result.MsiError,
        result.BumpedVersion,
        result.VersionError,
        result.FileCount,
        durationSeconds = Math.Round(result.Duration.TotalSeconds, 2),
        result.CommandLine,
        result.ErrorMessage,
    }, new JsonSerializerOptions { WriteIndented = true }));

    return result.Success ? 0 : 1;
}

/// <summary>只算版本递增，例：bump 1.4 1.4.2 1.4.65535 1.255.65535（用于核对自动递增规则）。</summary>
static int Bump(List<string> versions)
{
    if (versions.Count == 0)
    {
        Console.Error.WriteLine("用法：bump <版本> [<版本> ...]");
        return 2;
    }

    var results = new List<object>();
    foreach (var version in versions)
    {
        try
        {
            results.Add(new { version, next = MsiVersionService.Next(version) });
        }
        catch (PublishException ex)
        {
            Console.Error.WriteLine(version + " -> " + ex.Message);
            return 1;
        }
    }

    Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

/// <summary>把一个已经发布好的目录做成 MSI 安装包（本工具自身的安装包也走这条命令）。</summary>
static int PackMsi(List<string> arguments)
{
    var source = arguments.FirstOrDefault(a => !a.StartsWith("-"));
    if (string.IsNullOrWhiteSpace(source))
    {
        Console.Error.WriteLine("缺少发布目录。");
        return 2;
    }

    source = Path.GetFullPath(source!);
    var outputDirectory = GetOption(arguments, "--out") ?? Directory.GetCurrentDirectory();
    var options = BuildMsiOptions(arguments, GetOption(arguments, "--msi-version"));
    var projectName = options.ProductName ?? new DirectoryInfo(source).Name;
    var log = new Progress<string>(line => Console.WriteLine("[msi] " + line));

    try
    {
        var result = new MsiPackageBuilder().Create(
            source, outputDirectory, options, projectName, log, CancellationToken.None);

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            msiPath = result.MsiPath,
            result.ProductName,
            result.ProductVersion,
            result.ProductCode,
            result.UpgradeCode,
            result.FileCount,
            result.TotalBytes,
            result.Is64Bit,
            warnings = result.Warnings,
        }, new JsonSerializerOptions { WriteIndented = true }));

        return 0;
    }
    catch (PublishException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

static MsiOptions BuildMsiOptions(List<string> arguments, string? projectVersion) => new()
{
    Enabled = true,
    ProductName = GetOption(arguments, "--msi-product"),
    Manufacturer = GetOption(arguments, "--msi-manufacturer"),
    ProductVersion = GetOption(arguments, "--msi-version") ?? projectVersion,
    CreateShortcut = !HasFlag(arguments, "--no-msi-shortcut"),
    CreateDesktopShortcut = !HasFlag(arguments, "--no-msi-desktop-shortcut"),
    EntryExecutable = GetOption(arguments, "--msi-exe"),
    UpgradeCode = GetOption(arguments, "--msi-upgrade-code"),
    NamingMode = Enum.TryParse<ZipNamingMode>(GetOption(arguments, "--msi-mode"), ignoreCase: true, out var mode)
        ? mode
        : ZipNamingMode.ProjectNameVersion,
    CustomName = GetOption(arguments, "--msi-name"),
    AutoBumpVersion = HasFlag(arguments, "--msi-bump-version"),
    // 默认不写回：冒烟工具经常跑示例项目，别顺手改了仓库里的 csproj。
    WriteVersionBack = HasFlag(arguments, "--msi-write-version-back"),
};

static string? GetOption(List<string> arguments, string name)
{
    for (var i = 0; i < arguments.Count - 1; i++)
    {
        if (string.Equals(arguments[i], name, StringComparison.OrdinalIgnoreCase))
        {
            return arguments[i + 1];
        }
    }

    return null;
}

static bool HasFlag(List<string> arguments, string name) =>
    arguments.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
