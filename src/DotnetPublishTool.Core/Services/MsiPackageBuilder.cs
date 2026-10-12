using System.Globalization;
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

    private const int ComponentAttribute64Bit = 256;
    private const int FileAttributeVital = 512;

    /// <summary>Upgrade 表 Attributes：版本下限、上限都含端点。</summary>
    private const int UpgradeAttributes = 1 | 2;

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
        ("FindRelatedProducts", null, 100),
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
                new FileInfo(source).Length));
        }

        return payload;
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
                file.FileKey, componentKey, file.FileName, file.Size > int.MaxValue ? int.MaxValue : (int)file.Size, null, null, FileAttributeVital, sequence);
            database.Insert(
                "INSERT INTO `FeatureComponents` (`Feature_`,`Component_`) VALUES (?,?)",
                FeatureKey, componentKey);
        }

        database.Insert(
            "INSERT INTO `Media` (`DiskId`,`LastSequence`,`DiskPrompt`,`Cabinet`,`VolumeLabel`,`Source`) VALUES (?,?,?,?,?,?)",
            1, sequence, null, "#" + CabinetStreamName, null, null);

        // 同一升级代码 + 版本不高于本次版本 → 先按大版本升级卸掉旧版本。
        database.Insert(
            "INSERT INTO `Upgrade` (`UpgradeCode`,`VersionMin`,`VersionMax`,`Language`,`Attributes`,`Remove`,`ActionProperty`) VALUES (?,?,?,?,?,?,?)",
            spec.UpgradeCode, "0.0.0", spec.ProductVersion, null, UpgradeAttributes, null, UpgradeFoundProperty);

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

    private sealed record PayloadFile(string RelativePath, string FileKey, string FileName, string DirectoryKey, long Size);

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
