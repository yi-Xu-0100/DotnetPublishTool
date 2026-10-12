using System.Globalization;
using System.Diagnostics;
using System.Text;
using DotnetPublishTool.Core.Models;

namespace DotnetPublishTool.Core.Services;

/// <summary>MSI 生成结果。</summary>
public sealed class MsiBuildResult
{
    public required string MsiPath { get; init; }

    public required string ProductName { get; init; }

    public required string ProductVersion { get; init; }

    public required string ProductCode { get; init; }

    public required string UpgradeCode { get; init; }

    public required int FileCount { get; init; }

    public required long TotalBytes { get; init; }

    public required bool Is64Bit { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// 把一个发布目录做成「安装到 Program Files」的 MSI 安装包。
/// 只用系统自带的 msi.dll（直接写 Windows Installer 数据库）与 makecab.exe（压 cab），
/// 不依赖 WiX 之类的第三方工具链，也不需要访问网络。
/// </summary>
public sealed class MsiPackageBuilder
{
    /// <summary>cab 在 MSI 里的流名；Media.Cabinet 用 "#" + 流名 引用它，安装包因此只有一个文件。</summary>
    internal const string CabinetStreamName = "app.cab";

    private const string InstallFolderKey = "INSTALLFOLDER";
    private const string FeatureKey = "MainFeature";
    private const string ShortcutKey = "SC_MainApplication";
    private const string MenuFolderKey = "ApplicationProgramsFolder";
    private const string InstallFolderInProgramFiles64 = "ProgramFiles64Folder";
    private const string InstallFolderInProgramFiles32 = "ProgramFilesFolder";
    private const string UpgradeFoundProperty = "UPGRADEFOUND";

    private const string NewerVersionFoundProperty = "NEWERVERSIONDETECTED";

    private const int ComponentAttribute64Bit = 256;
    private const int FileAttributeVital = 512;

    /// <summary>
    /// Upgrade 表 Attributes 的位（来自 msi.h）：
    ///   0x0001 MigrateFeatures、0x0002 OnlyDetect（只检测、**不卸载**）、0x0004 IgnoreRemoveFailure、
    ///   0x0100 VersionMinInclusive、0x0200 VersionMaxInclusive、0x0400 LanguagesExclusive。
    /// 这里曾经错写成 1 | 2，等于给「要卸载旧版」的那一行加了 OnlyDetect —— 于是 FindRelatedProducts 能检测到
    /// （UPGRADEFOUND 有值），RemoveExistingProducts 却什么都不做（2 毫秒空转）。2026-10-13 拿本机真实产品的
    /// Upgrade 表对照（它们用 256 / 258）才定位到。
    /// </summary>
    private const int UpgradeAttributes = 0x0100;

    /// <summary>只检测、不卸载：用于「已经装了更新的版本」那条防降级记录。</summary>
    private const int UpgradeAttributesOnlyDetect = 0x0002;

    /// <summary>
    /// 卸载旧版本那一行的 Attributes。诊断用环境变量 <c>DOTNETPUBLISHTOOL_UPGRADE_ATTRIBUTES</c> 可以覆盖它 ——
    /// <c>tools/verify-msi-upgrade.ps1 -ProbeVariants</c> 正是靠它重打变体包，复现「带 OnlyDetect(0x2) 就只检测不卸载」。
    /// 正常打包不要设置这个变量。
    /// </summary>
    private static int ResolveUpgradeAttributes()
    {
        var raw = Environment.GetEnvironmentVariable("DOTNETPUBLISHTOOL_UPGRADE_ATTRIBUTES");
        if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out var value) && value > 0)
        {
            return value;
        }

        return UpgradeAttributes;
    }

    /// <summary>
    /// Error 表内容：安装/卸载/升级常见失败码的中文说明。
    /// 系统里没有可直接提取的「标准 Error 表」（msimsg.dll 只有 1603 这类少量文案，1101/1925 的正确文案不在里面），
    /// 所以这里是我们自己的措辞：不带 [n] 占位符，避免被 MSI 替换成空；数字编号仍由 MSI 自己打印。
    /// </summary>
    private static readonly (int Id, string Message)[] StandardErrors =
    [
        (16, "无法删除目录：目录可能正在被使用。"),
        (1101, "无法打开注册表项：可能没有权限（试试以管理员身份运行安装程序）。"),
        (1303, "无法创建或写入文件：请以管理员身份运行，或先关闭正在使用该文件的程序。"),
        (1304, "同名文件已存在且无法覆盖：请先卸载旧版本再安装。"),
        (1305, "读取安装包里的文件时出错：安装包可能不完整，请重新下载。"),
        (1316, "写入文件失败：目标文件可能被占用，请先关闭正在运行的程序。"),
        (1603, "安装时发生严重错误。常见原因：没有管理员权限、目标程序正在运行、找不到安装包。用 msiexec /l*v 生成详细日志可看到具体原因。"),
        (1619, "无法打开安装包：文件可能损坏或不完整，请重新下载。"),
        (1620, "安装包无效：请确认下载完整。"),
        (1633, "当前系统不支持这个安装包（可能是 32/64 位不匹配）。"),
        (1638, "本产品的另一个版本已经安装：请先在「程序和功能」里卸载它，再安装这个版本。"),
        (1641, "安装已开始，需要重启计算机才能全部生效。"),
        (1701, "无法创建快捷方式：目标文件夹不可写（试试以管理员身份运行安装程序）。"),
        (1925, "需要管理员权限才能为所有用户安装：请右键「以管理员身份运行」，或改用每用户安装。"),
        (1926, "没有写入权限：请以管理员身份运行安装程序。"),
        (2205, "安装包数据库里缺少表（打包时有东西没写全）。"),
        (2228, "安装包数据库里缺少列（打包时有东西没写全）。"),
    ];

    // 列定义抄自 Windows Installer 的官方 schema（对齐 WiX 产出的包）：
    //   - 用户可见文本列要标 LOCALIZABLE，否则引擎读这些列时会报错；
    //   - 整数列的宽度也要一致（FileSize/Sequence/LastSequence/Upgrade.Attributes 是 LONG 而不是 SHORT）。
    private static readonly string[] Schema =
    [
        "CREATE TABLE `Property` (`Property` CHAR(72) NOT NULL, `Value` CHAR(0) NOT NULL LOCALIZABLE PRIMARY KEY `Property`)",
        "CREATE TABLE `Directory` (`Directory` CHAR(72) NOT NULL, `Directory_Parent` CHAR(72), `DefaultDir` CHAR(255) NOT NULL LOCALIZABLE PRIMARY KEY `Directory`)",
        "CREATE TABLE `Feature` (`Feature` CHAR(38) NOT NULL, `Feature_Parent` CHAR(38), `Title` CHAR(64) LOCALIZABLE, `Description` CHAR(255) LOCALIZABLE, `Display` SHORT, `Level` SHORT NOT NULL, `Directory_` CHAR(72), `Attributes` SHORT NOT NULL PRIMARY KEY `Feature`)",
        "CREATE TABLE `Component` (`Component` CHAR(72) NOT NULL, `ComponentId` CHAR(38), `Directory_` CHAR(72), `Attributes` SHORT NOT NULL, `Condition` CHAR(255), `KeyPath` CHAR(72) PRIMARY KEY `Component`)",
        "CREATE TABLE `File` (`File` CHAR(72) NOT NULL, `Component_` CHAR(72), `FileName` CHAR(255) NOT NULL LOCALIZABLE, `FileSize` LONG NOT NULL, `Version` CHAR(72), `Language` CHAR(20), `Attributes` SHORT, `Sequence` LONG NOT NULL PRIMARY KEY `File`)",
        "CREATE TABLE `FeatureComponents` (`Feature_` CHAR(38) NOT NULL, `Component_` CHAR(72) NOT NULL PRIMARY KEY `Feature_`, `Component_`)",
        "CREATE TABLE `Media` (`DiskId` SHORT NOT NULL, `LastSequence` LONG NOT NULL, `DiskPrompt` CHAR(64) LOCALIZABLE, `Cabinet` CHAR(255), `VolumeLabel` CHAR(32), `Source` CHAR(72) PRIMARY KEY `DiskId`)",
        "CREATE TABLE `Upgrade` (`UpgradeCode` CHAR(38) NOT NULL, `VersionMin` CHAR(20), `VersionMax` CHAR(20), `Language` CHAR(255), `Attributes` LONG NOT NULL, `Remove` CHAR(255), `ActionProperty` CHAR(72) NOT NULL PRIMARY KEY `UpgradeCode`, `VersionMin`, `VersionMax`, `Language`, `Attributes`)",
        "CREATE TABLE `LaunchCondition` (`Condition` CHAR(255) NOT NULL, `Description` CHAR(255) NOT NULL LOCALIZABLE PRIMARY KEY `Condition`)",
        // Error 表：安装/卸载/升级失败时，MSI 从这张表取文案写进日志与弹窗。
        // 少了它，失败在日志里只剩「SELECT Message FROM Error WHERE Error = 22」这类查表失败，看不到原因。
        "CREATE TABLE `Error` (`Error` LONG NOT NULL, `Message` CHAR(0) NOT NULL LOCALIZABLE PRIMARY KEY `Error`)",
        "CREATE TABLE `Shortcut` (`Shortcut` CHAR(72) NOT NULL, `Directory_` CHAR(72) NOT NULL, `Name` CHAR(128) NOT NULL LOCALIZABLE, `Component_` CHAR(72) NOT NULL, `Target` CHAR(72) NOT NULL, `Arguments` CHAR(255), `Description` CHAR(255) LOCALIZABLE, `Hotkey` SHORT, `Icon_` CHAR(72), `IconIndex` SHORT, `ShowCmd` SHORT, `WkDir` CHAR(72) PRIMARY KEY `Shortcut`)",
        "CREATE TABLE `InstallExecuteSequence` (`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` SHORT PRIMARY KEY `Action`)",
        "CREATE TABLE `InstallUISequence` (`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` SHORT PRIMARY KEY `Action`)",
        // 管理安装（msiexec /a，常用来生成网络/组策略部署用的「源映像」）走的是 Admin*Sequence；
        // 少了这两张表，/a 会「成功但什么都不解出来」。
        "CREATE TABLE `AdminExecuteSequence` (`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` SHORT PRIMARY KEY `Action`)",
        "CREATE TABLE `AdminUISequence` (`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` SHORT PRIMARY KEY `Action`)",
    ];

    /// <summary>
    /// Windows Installer 的标准动作顺序。RemoveExistingProducts 排在 InstallInitialize 之后（大版本升级：
    /// 新版本装好后由 RemoveExistingProducts 卸掉旧版本）。InstallUISequence 里的 ExecuteAction 负责
    /// 把「界面序列」接到「执行序列」——少了它双击安装会什么都不做。
    /// </summary>
    private static readonly (string Action, string? Condition, int Sequence)[] ExecuteSequence =
    [
        ("FindRelatedProducts", null, 25),
        ("LaunchConditions", null, 100),
        ("ValidateProductID", null, 700),
        ("CostInitialize", null, 800),
        ("FileCost", null, 900),
        ("CostFinalize", null, 1000),
        ("MigrateFeatureStates", null, 1200),
        ("InstallValidate", null, 1400),
        ("InstallInitialize", null, 1500),
        ("RemoveExistingProducts", null, 1501),
        ("ProcessComponents", null, 1600),
        ("UnpublishFeatures", null, 1800),
        ("RemoveShortcuts", "Installed AND NOT UPGRADINGPRODUCTCODE", 3200),
        ("RemoveFiles", null, 3500),
        ("InstallFiles", null, 4000),
        ("CreateShortcuts", null, 4500),
        ("RegisterProduct", null, 6100),
        ("PublishFeatures", null, 6300),
        ("PublishProduct", null, 6400),
        ("InstallFinalize", null, 6600),
    ];

    private static readonly (string Action, string? Condition, int Sequence)[] UiSequence =
    [
        ("FindRelatedProducts", null, 100),
        ("CostInitialize", null, 800),
        ("FileCost", null, 900),
        ("CostFinalize", null, 1000),
        ("ExecuteAction", null, 1300),
    ];

    /// <summary>管理安装（/a）的动作序列。InstallAdminPackage 才是真正把文件铺到目标目录的动作。</summary>
    private static readonly (string Action, string? Condition, int Sequence)[] AdminExecuteSequence =
    [
        ("CostInitialize", null, 800),
        ("FileCost", null, 900),
        ("CostFinalize", null, 1000),
        ("InstallValidate", null, 1400),
        ("InstallInitialize", null, 1500),
        ("InstallAdminPackage", null, 3900),
        ("InstallFiles", null, 4000),
        ("InstallFinalize", null, 6600),
    ];

    private static readonly (string Action, string? Condition, int Sequence)[] AdminUiSequence =
    [
        ("CostInitialize", null, 800),
        ("FileCost", null, 900),
        ("CostFinalize", null, 1000),
        ("ExecuteAction", null, 1300),
    ];

    /// <summary>
    /// 生成安装包。sourceDirectory 是发布产物目录，outputDirectory 是安装包落盘目录（为空则用当前目录）。
    /// </summary>
    public MsiBuildResult Create(
        string sourceDirectory,
        string? outputDirectory,
        MsiOptions options,
        string projectName,
        IProgress<string>? log,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            throw new PublishException("发布目录不存在，无法生成 MSI：" + sourceDirectory);
        }

        var productName = FirstNonBlank(options.ProductName, projectName);
        var manufacturer = FirstNonBlank(options.Manufacturer, productName);
        var version = MsiVersion.Normalize(options.ProductVersion ?? options.Version);
        var upgradeCode = string.IsNullOrWhiteSpace(options.UpgradeCode)
            ? StableGuid.ToMsiString(StableGuid.From(manufacturer + "|" + productName))
            : StableGuid.Normalize(options.UpgradeCode!, "升级代码");
        var productCode = StableGuid.ToMsiString(StableGuid.From(upgradeCode + "|" + version));
        // 摘要信息里的 Revision Number 必须是带花括号的 GUID（包码）——所有正常安装包都是这个格式。
        var packageCode = "{" + Guid.NewGuid().ToString("D").ToUpperInvariant() + "}";
        var installFolderName = PathLayout.Sanitize(productName);

        var sourceFiles = Directory
            .EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .OrderBy(path => Path.GetRelativePath(sourceDirectory, path), StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (sourceFiles.Count == 0)
        {
            throw new PublishException("发布目录里没有任何文件，无法生成 MSI：" + sourceDirectory);
        }

        var warnings = new List<string>();
        var entryExecutable = ResolveEntryExecutable(sourceDirectory, sourceFiles, options.EntryExecutable, productName, projectName);
        var is64Bit = (entryExecutable is null ? null : PeArchitecture.Is64Bit(entryExecutable))
            ?? Environment.Is64BitOperatingSystem;
        if (entryExecutable is null)
        {
            warnings.Add("发布目录里没有找到 .exe，安装包不会创建开始菜单快捷方式。");
        }

        var stagingDirectory = Path.Combine(Path.GetTempPath(), "DotnetPublishTool-msi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            var directories = new DirectoryTable();
            var payload = StageFiles(sourceDirectory, stagingDirectory, sourceFiles, directories, warnings);
            cancellationToken.ThrowIfCancellationRequested();

            log?.Report($"[MSI] 正在压缩 {payload.Count} 个文件…");
            CabinetBuilder.Create(stagingDirectory, CabinetStreamName, payload.Select(file => file.FileKey).ToList(), log);
            var cabinetPath = Path.Combine(stagingDirectory, CabinetStreamName);

            var createShortcut = options.CreateShortcut && entryExecutable is not null;
            var shortcutFileKey = createShortcut
                ? payload.FirstOrDefault(file => string.Equals(file.RelativePath, Path.GetRelativePath(sourceDirectory, entryExecutable!), StringComparison.OrdinalIgnoreCase))?.FileKey
                : null;
            if (createShortcut && shortcutFileKey is null)
            {
                createShortcut = false;
                warnings.Add("没有在发布产物里定位到启动程序，安装包不会创建开始菜单快捷方式。");
            }

            var spec = new PackageSpec
            {
                ProductName = productName,
                Manufacturer = manufacturer,
                ProductVersion = version,
                ProductCode = productCode,
                UpgradeCode = upgradeCode,
                PackageCode = packageCode,
                InstallFolderName = installFolderName,
                Directories = directories.Rows,
                Files = payload,
                CabinetPath = cabinetPath,
                Is64Bit = is64Bit,
                ShortcutTargetFileKey = shortcutFileKey,
            };

            var msiName = ZipNaming.Resolve(
                options.NamingMode, options.DateFormat, options.CustomName, version, productName, ".msi");
            var targetDirectory = string.IsNullOrWhiteSpace(outputDirectory)
                ? Directory.GetCurrentDirectory()
                : Path.GetFullPath(outputDirectory!);
            var msiPath = Path.Combine(targetDirectory, msiName);
            var stagedMsiPath = Path.Combine(stagingDirectory, "package.msi");

            log?.Report($"[MSI] 正在写入安装数据库（{(is64Bit ? "64 位" : "32 位")}，安装到 {(is64Bit ? "Program Files" : "Program Files (x86)")}）…");
            WriteDatabase(stagedMsiPath, spec);

            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(targetDirectory);
            if (File.Exists(msiPath))
            {
                File.Delete(msiPath);
            }

            File.Move(stagedMsiPath, msiPath);

            var totalBytes = payload.Sum(file => file.Size);
            return new MsiBuildResult
            {
                MsiPath = msiPath,
                ProductName = productName,
                ProductVersion = version,
                ProductCode = productCode,
                UpgradeCode = upgradeCode,
                FileCount = payload.Count,
                TotalBytes = totalBytes,
                Is64Bit = is64Bit,
                Warnings = warnings,
            };
        }
        finally
        {
            // 排障用：设 DOTNETPUBLISHTOOL_KEEP_MSI_STAGING=1 时保留暂存目录（含 cab 与未搬走的 msi）。
            if (Environment.GetEnvironmentVariable("DOTNETPUBLISHTOOL_KEEP_MSI_STAGING") == "1")
            {
                log?.Report("[MSI] 已按 DOTNETPUBLISHTOOL_KEEP_MSI_STAGING=1 保留暂存目录：" + stagingDirectory);
            }
            else
            {
                TryDeleteDirectory(stagingDirectory);
            }
        }
    }

    /// <summary>把发布目录里的文件复制到暂存目录，复制后的名字（File 键）就是 cab 里的条目名。</summary>
    private static List<PayloadFile> StageFiles(
        string sourceDirectory,
        string stagingDirectory,
        IReadOnlyList<string> sourceFiles,
        DirectoryTable directories,
        List<string> warnings)
    {
        var payload = new List<PayloadFile>(sourceFiles.Count);
        for (var index = 0; index < sourceFiles.Count; index++)
        {
            var source = sourceFiles[index];
            var relative = Path.GetRelativePath(sourceDirectory, source);
            var fileName = Path.GetFileName(relative);
            var fileKey = MakeFileKey(index + 1, fileName);
            File.Copy(source, Path.Combine(stagingDirectory, fileKey), overwrite: true);
            payload.Add(new PayloadFile(
                relative,
                fileKey,
                fileName,
                directories.Resolve(Path.GetDirectoryName(relative) ?? string.Empty),
                new FileInfo(source).Length,
                ReadFileVersion(source)));
        }

        return payload;
    }

    /// <summary>
    /// 读文件自身的版本号，四段写进 File 表的 Version 列。
    /// 这一列不能空：Windows Installer 判断「要不要覆盖已存在的同名文件」时只看 File 表的 Version ——
    /// 空了就当成「无版本文件」，改用文件日期判断，目标文件的日期更新时**不会覆盖**，
    /// 表现就是「装了新版本但程序还是旧的」。
    /// 没有版本资源的文件（json 等）返回 null，仍按日期规则走。
    /// </summary>
    private static string? ReadFileVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            if (string.IsNullOrWhiteSpace(info.FileVersion))
            {
                return null;
            }

            var parts = new[] { info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart };
            return parts.All(part => part == 0) ? null : string.Join('.', parts);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 打完自检：下面每一条都对应一次真实踩坑（2026-10-12 / 10-13），错一条就会产出「装得上、升级却不生效」的包，
    /// 所以宁可当场报错，也不要把这种包发出去：
    ///   · 卸旧版那一行带 OnlyDetect(0x2) → 引擎只检测不卸载，旧记录与旧文件都留着；
    ///   · File 表 Version 没写 → 当成无版本文件按日期比较，新版本的文件装不上；
    ///   · 少了 Error / LaunchCondition 表 → 失败只有错误码没文案、防降级提示失声。
    /// 诊断开关 DOTNETPUBLISHTOOL_UPGRADE_ATTRIBUTES 生效时，OnlyDetect 那条降级成警告（验证脚本要用它造变体包）。
    /// </summary>
    private static void ValidatePackage(MsiDatabase database, PackageSpec spec, IProgress<string>? log)
    {
        var problems = new List<string>();
        var diagnosticOverride = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTNETPUBLISHTOOL_UPGRADE_ATTRIBUTES"));

        var upgradeRows = database.Query("SELECT `ActionProperty`,`Attributes` FROM `Upgrade`");
        var removalRows = upgradeRows.Where(row => row.Length > 1 && row[0] == UpgradeFoundProperty).ToList();
        if (removalRows.Count == 0)
        {
            problems.Add("Upgrade 表缺少卸载旧版本那一行（ActionProperty=" + UpgradeFoundProperty + "）");
        }

        foreach (var row in removalRows)
        {
            int.TryParse(row[1], out var attributes);
            if ((attributes & UpgradeAttributesOnlyDetect) == 0)
            {
                continue;
            }

            var message = "卸载旧版本那一行的 Attributes 带了 OnlyDetect(0x2)（值 " + row[1] + "）：引擎只会检测、不会卸载旧版本";
            if (diagnosticOverride)
            {
                log?.Report("[MSI][诊断] " + message + "。诊断开关已开启，仅警告。");
            }
            else
            {
                problems.Add(message);
            }
        }

        if (!upgradeRows.Any(row => row.Length > 0 && row[0] == NewerVersionFoundProperty))
        {
            problems.Add("Upgrade 表缺少防降级那一行（ActionProperty=" + NewerVersionFoundProperty + "）");
        }

        var writtenVersions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in database.Query("SELECT `File`,`Version` FROM `File`"))
        {
            if (row.Length > 1)
            {
                writtenVersions[row[0]] = row[1];
            }
        }

        foreach (var file in spec.Files.Where(file => !string.IsNullOrEmpty(file.Version)))
        {
            writtenVersions.TryGetValue(file.FileKey, out var written);
            if (!string.Equals(written, file.Version, StringComparison.Ordinal))
            {
                problems.Add("File 表的 Version 没写对：" + file.FileName + " 应为 " + file.Version + "，实际是 " + (written ?? "(空)"));
                break;
            }
        }

        if (database.Query("SELECT `Error` FROM `Error`").Count == 0)
        {
            problems.Add("Error 表是空的：安装失败时日志里只有错误码、没有文案");
        }

        if (database.Query("SELECT `Condition` FROM `LaunchCondition`").Count == 0)
        {
            problems.Add("LaunchCondition 表是空的：装了更新版本时不会有任何提示");
        }

        if (problems.Count > 0)
        {
            throw new PublishException(
                "生成的 MSI 自检没通过（这些问题会让「升级」看起来装成功、实际没生效）："
                + Environment.NewLine + "- " + string.Join(Environment.NewLine + "- ", problems));
        }
    }

    /// <summary>File 键要唯一、ASCII 且可读；同名文件靠序号前缀区分。</summary>
    private static string MakeFileKey(int index, string fileName)
    {
        var stem = ToAscii(Path.GetFileNameWithoutExtension(fileName));
        var extension = ToAscii(Path.GetExtension(fileName));
        if (stem.Length > 40)
        {
            stem = stem[..40];
        }

        var key = "f" + index.ToString("D4", CultureInfo.InvariantCulture) + "_" + stem + extension;
        return key.Length <= 72 ? key : key[..72];
    }

    private static string ToAscii(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            var usable = character is >= ' ' and <= '~' && character is not ('"' or '\\' or '/' or '|' or '*' or '?' or '<' or '>');
            builder.Append(usable ? character : '_');
        }

        return builder.ToString();
    }

    private static string? ResolveEntryExecutable(
        string sourceDirectory,
        IReadOnlyList<string> sourceFiles,
        string? configured,
        string productName,
        string projectName)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var candidate = Path.GetFullPath(Path.Combine(sourceDirectory, configured!));
            if (!File.Exists(candidate))
            {
                throw new PublishException("MSI 里指定的启动程序在发布产物中不存在：" + configured);
            }

            return candidate;
        }

        var rootExecutables = sourceFiles
            .Where(file => string.Equals(Path.GetDirectoryName(Path.GetRelativePath(sourceDirectory, file)) ?? string.Empty, string.Empty, StringComparison.OrdinalIgnoreCase))
            .Where(file => string.Equals(Path.GetExtension(file), ".exe", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return rootExecutables.FirstOrDefault(file =>
                string.Equals(Path.GetFileNameWithoutExtension(file), productName, StringComparison.OrdinalIgnoreCase))
            ?? rootExecutables.FirstOrDefault(file =>
                string.Equals(Path.GetFileNameWithoutExtension(file), projectName, StringComparison.OrdinalIgnoreCase))
            ?? rootExecutables.FirstOrDefault();
    }

    private static void WriteDatabase(string databasePath, PackageSpec spec)
    {
        using var database = new MsiDatabase(databasePath);
        foreach (var sql in Schema)
        {
            database.Execute(sql);
        }

        if (!database.TableExists("_Streams"))
        {
            database.Execute("CREATE TABLE `_Streams` (`Name` CHAR(62) NOT NULL, `Data` LONGCHAR NOT NULL PRIMARY KEY `Name`)");
        }

        InsertProperty(database, "ProductName", spec.ProductName);
        InsertProperty(database, "ProductCode", spec.ProductCode);
        InsertProperty(database, "ProductVersion", spec.ProductVersion);
        InsertProperty(database, "ProductLanguage", "1033");
        InsertProperty(database, "Manufacturer", spec.Manufacturer);
        InsertProperty(database, "UpgradeCode", spec.UpgradeCode);
        InsertProperty(database, "ALLUSERS", "1");
        InsertProperty(database, "ARPCOMMENTS", "由 DotnetPublishTool 生成");
        InsertProperty(database, "ARPNOMODIFY", "1");

        var programFilesFolder = spec.Is64Bit ? InstallFolderInProgramFiles64 : InstallFolderInProgramFiles32;
        database.Insert("INSERT INTO `Directory` (`Directory`,`Directory_Parent`,`DefaultDir`) VALUES (?,?,?)", "TARGETDIR", null, "SourceDir");
        database.Insert("INSERT INTO `Directory` (`Directory`,`Directory_Parent`,`DefaultDir`) VALUES (?,?,?)", programFilesFolder, "TARGETDIR", ".");
        database.Insert("INSERT INTO `Directory` (`Directory`,`Directory_Parent`,`DefaultDir`) VALUES (?,?,?)", InstallFolderKey, programFilesFolder, spec.InstallFolderName);
        foreach (var row in spec.Directories)
        {
            database.Insert("INSERT INTO `Directory` (`Directory`,`Directory_Parent`,`DefaultDir`) VALUES (?,?,?)", row.Key, row.ParentKey, row.DefaultDir);
        }

        var withShortcut = spec.ShortcutTargetFileKey is not null;
        if (withShortcut)
        {
            database.Insert("INSERT INTO `Directory` (`Directory`,`Directory_Parent`,`DefaultDir`) VALUES (?,?,?)", "ProgramMenuFolder", "TARGETDIR", ".");
            database.Insert("INSERT INTO `Directory` (`Directory`,`Directory_Parent`,`DefaultDir`) VALUES (?,?,?)", MenuFolderKey, "ProgramMenuFolder", spec.InstallFolderName);
        }

        database.Insert(
            "INSERT INTO `Feature` (`Feature`,`Feature_Parent`,`Title`,`Description`,`Display`,`Level`,`Directory_`,`Attributes`) VALUES (?,?,?,?,?,?,?,?)",
            FeatureKey, null, spec.ProductName, spec.ProductName, 1, 1, InstallFolderKey, 0);

        foreach (var (errorId, message) in StandardErrors)
        {
            database.Insert("INSERT INTO `Error` (`Error`,`Message`) VALUES (?,?)", errorId, message);
        }

        var sequence = 0;
        foreach (var file in spec.Files)
        {
            sequence++;
            var componentKey = "c_" + file.FileKey;
            var componentId = StableGuid.ToMsiString(StableGuid.From(spec.UpgradeCode + "|" + file.RelativePath));
            database.Insert(
                "INSERT INTO `Component` (`Component`,`ComponentId`,`Directory_`,`Attributes`,`Condition`,`KeyPath`) VALUES (?,?,?,?,?,?)",
                componentKey, componentId, file.DirectoryKey, spec.Is64Bit ? ComponentAttribute64Bit : 0, null, file.FileKey);
            database.Insert(
                "INSERT INTO `File` (`File`,`Component_`,`FileName`,`FileSize`,`Version`,`Language`,`Attributes`,`Sequence`) VALUES (?,?,?,?,?,?,?,?)",
                file.FileKey, componentKey, file.FileName, file.Size > int.MaxValue ? int.MaxValue : (int)file.Size, file.Version, null, FileAttributeVital, sequence);
            database.Insert(
                "INSERT INTO `FeatureComponents` (`Feature_`,`Component_`) VALUES (?,?)",
                FeatureKey, componentKey);
        }

        database.Insert(
            "INSERT INTO `Media` (`DiskId`,`LastSequence`,`DiskPrompt`,`Cabinet`,`VolumeLabel`,`Source`) VALUES (?,?,?,?,?,?)",
            1, sequence, null, "#" + CabinetStreamName, null, null);

        // 1) 卸载旧版本：范围 [0.0.0, 本版本)（上限不含，正好不会匹配到自己）。
        //    Attributes 必须是 VersionMinInclusive，**不能带 OnlyDetect**，否则只检测不卸载。
        database.Insert(
            "INSERT INTO `Upgrade` (`UpgradeCode`,`VersionMin`,`VersionMax`,`Language`,`Attributes`,`Remove`,`ActionProperty`) VALUES (?,?,?,?,?,?,?)",
            spec.UpgradeCode, "0.0.0", spec.ProductVersion, null, ResolveUpgradeAttributes(), null, UpgradeFoundProperty);

        // 2) 防降级：装了比本包更新的版本时给明确提示，而不是静默走维护模式什么都不做（OnlyDetect 只检测）。
        database.Insert(
            "INSERT INTO `Upgrade` (`UpgradeCode`,`VersionMin`,`VersionMax`,`Language`,`Attributes`,`Remove`,`ActionProperty`) VALUES (?,?,?,?,?,?,?)",
            spec.UpgradeCode, spec.ProductVersion, null, null, UpgradeAttributes | UpgradeAttributesOnlyDetect, null, NewerVersionFoundProperty);

        database.Insert(
            "INSERT INTO `LaunchCondition` (`Condition`,`Description`) VALUES (?,?)",
            "NOT " + NewerVersionFoundProperty,
            "系统里已经装了更新版本的 " + spec.ProductName + "。请先在「程序和功能」里卸载它，再安装这个版本。");

        if (withShortcut)
        {
            database.Insert(
                "INSERT INTO `Shortcut` (`Shortcut`,`Directory_`,`Name`,`Component_`,`Target`,`Arguments`,`Description`,`Hotkey`,`Icon_`,`IconIndex`,`ShowCmd`,`WkDir`) VALUES (?,?,?,?,?,?,?,?,?,?,?,?)",
                ShortcutKey, MenuFolderKey, spec.ProductName, "c_" + spec.ShortcutTargetFileKey!,
                "[!" + spec.ShortcutTargetFileKey + "]", null, spec.ProductName, null, null, null, null, InstallFolderKey);
        }

        foreach (var (action, condition, at) in ExecuteSequence)
        {
            database.Insert(
                "INSERT INTO `InstallExecuteSequence` (`Action`,`Condition`,`Sequence`) VALUES (?,?,?)",
                action, condition, at);
        }

        foreach (var (action, condition, at) in UiSequence)
        {
            database.Insert(
                "INSERT INTO `InstallUISequence` (`Action`,`Condition`,`Sequence`) VALUES (?,?,?)",
                action, condition, at);
        }

        foreach (var (action, condition, at) in AdminExecuteSequence)
        {
            database.Insert(
                "INSERT INTO `AdminExecuteSequence` (`Action`,`Condition`,`Sequence`) VALUES (?,?,?)",
                action, condition, at);
        }

        foreach (var (action, condition, at) in AdminUiSequence)
        {
            database.Insert(
                "INSERT INTO `AdminUISequence` (`Action`,`Condition`,`Sequence`) VALUES (?,?,?)",
                action, condition, at);
        }

        database.InsertStream(CabinetStreamName, spec.CabinetPath);
        database.WriteSummaryInformation(
            spec.ProductName, spec.Manufacturer, spec.Is64Bit ? "x64;1033" : "Intel;1033", spec.PackageCode);
        ValidatePackage(database, spec, null);

        database.Commit();
    }

    private static void InsertProperty(MsiDatabase database, string name, string value) =>
        database.Insert("INSERT INTO `Property` (`Property`,`Value`) VALUES (?,?)", name, value);

    private static string FirstNonBlank(string? preferred, string fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred!.Trim();

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // 暂存目录清不掉不影响结果。
        }
    }

    private sealed record DirectoryRow(string Key, string ParentKey, string DefaultDir);

    private sealed record PayloadFile(
        string RelativePath,
        string FileKey,
        string FileName,
        string DirectoryKey,
        long Size,
        string? Version);

    private sealed class PackageSpec
    {
        public required string ProductName { get; init; }

        public required string Manufacturer { get; init; }

        public required string ProductCode { get; init; }

        public required string UpgradeCode { get; init; }

        public required string PackageCode { get; init; }

        public required string ProductVersion { get; init; }

        public required string InstallFolderName { get; init; }

        public required IReadOnlyList<DirectoryRow> Directories { get; init; }

        public required IReadOnlyList<PayloadFile> Files { get; init; }

        public required string CabinetPath { get; init; }

        public required bool Is64Bit { get; init; }

        public string? ShortcutTargetFileKey { get; init; }
    }

    /// <summary>发布产物里的子目录 → Directory 表行（父目录先于子目录出现）。</summary>
    private sealed class DirectoryTable
    {
        private readonly Dictionary<string, string> _keys = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<DirectoryRow> _rows = [];
        private int _next;

        public IReadOnlyList<DirectoryRow> Rows => _rows;

        public string Resolve(string relativeDirectory)
        {
            var normalized = (relativeDirectory ?? string.Empty).Replace('/', '\\').Trim('\\');
            if (normalized.Length == 0)
            {
                return InstallFolderKey;
            }

            if (_keys.TryGetValue(normalized, out var existing))
            {
                return existing;
            }

            var parent = Resolve(Path.GetDirectoryName(normalized) ?? string.Empty);
            var key = "DIR_" + _next.ToString("D4", CultureInfo.InvariantCulture);
            _next++;
            _keys[normalized] = key;
            _rows.Add(new DirectoryRow(key, parent, Path.GetFileName(normalized)));
            return key;
        }
    }
}
