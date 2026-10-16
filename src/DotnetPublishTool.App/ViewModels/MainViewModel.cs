using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using DotnetPublishTool.App.Services;
using DotnetPublishTool.Core.Models;
using DotnetPublishTool.Core.Services;

namespace DotnetPublishTool.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private static readonly string[] KnownRuntimeIdentifiers =
    [
        "win-x64", "win-x86", "win-arm64",
        "linux-x64", "linux-arm64",
        "osx-x64", "osx-arm64",
    ];

    private readonly IDialogService _dialogs;
    private readonly AppSettingsService _settingsService;
    private readonly ProjectInspector _inspector;
    private readonly PublishProfileStore _profileStore;
    private readonly PublishWorkflow _workflow;
    private readonly MsiVersionService _versionService;
    private readonly StringBuilder _logBuilder = new();
    private readonly object _logLock = new();

    private const int MaxRecentProjects = 20;

    private CancellationTokenSource? _cancellation;
    private string? _lastProjectPath;
    private bool _initialized;
    private RecentProject? _selectedRecentProject;

    private string _projectPath = string.Empty;
    private ProjectInfo? _selectedProject;
    private PublishProfile? _selectedProfile;

    private string _outputRoot = PathLayout.DefaultOutputRoot;
    private string _configuration = "Release";
    private string? _targetFramework;
    private string? _runtimeIdentifier;
    private bool _selfContained;
    private bool _publishSingleFile;
    private bool _publishReadyToRun;
    private bool _publishTrimmed;
    private string _environmentName = string.Empty;
    private string _versionOverride = string.Empty;
    private bool _cleanPublishDirectory = true;
    private bool _noRestore;

    private bool _makeZip = true;
    private ZipNamingMode _zipNamingMode = ZipNamingMode.ProjectNameDate;
    private string _zipDateFormat = ZipOptions.DefaultDateFormat;
    private string _customZipName = string.Empty;
    private bool _zipIncludeRootFolder;

    private bool _makeMsi;
    private string _msiProductName = string.Empty;
    private string _msiManufacturer = string.Empty;
    private bool _msiCreateShortcut = true;
    private bool _msiCreateDesktopShortcut = true;
    private ZipNamingMode _msiNamingMode = ZipNamingMode.ProjectNameVersion;
    private string _msiCustomName = string.Empty;
    private bool _msiAutoBumpVersion;
    private bool _msiAutoBumpVersionOnce;

    /// <summary>常用设置/发布配置当前是为哪个项目加载的（同一个项目刷新时不要重载）。</summary>
    private string? _profilesLoadedFor;
    private bool _msiWriteVersionBack = true;

    private string _profilePreviewXml = string.Empty;
    private string _logText = string.Empty;
    private string _statusText = "请选择一个 dotnet 项目或解决方案。";
    private bool _isBusy;

    public MainViewModel(
        IDialogService dialogs,
        AppSettingsService settingsService,
        ProjectInspector inspector,
        PublishProfileStore profileStore,
        PublishWorkflow workflow,
        MsiVersionService versionService)
    {
        _dialogs = dialogs;
        _settingsService = settingsService;
        _inspector = inspector;
        _profileStore = profileStore;
        _workflow = workflow;
        _versionService = versionService;

        BrowseProjectCommand = new AsyncRelayCommand(BrowseProjectAsync, () => !IsBusy);
        RescanCommand = new AsyncRelayCommand(RescanAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(ProjectPath));
        BrowseOutputRootCommand = new RelayCommand(BrowseOutputRoot, () => !IsBusy);
        ClearLogCommand = new RelayCommand(ClearLog);
        ClearRecentProjectsCommand = new RelayCommand(ClearRecentProjects, () => RecentProjects.Count > 0);
        SaveProfileCommand = new RelayCommand(() => SaveProfileOnly(), () => !IsBusy && SelectedProject is not null);
        OpenProjectFolderCommand = new RelayCommand(OpenProjectFolder, () => SelectedProject is not null);
        OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder, () => SelectedProject is not null);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        PublishCommand = new AsyncRelayCommand(PublishAsync, () => !IsBusy && SelectedProject is not null);

        ExtraProperties.CollectionChanged += OnExtraPropertiesChanged;
    }

    public ObservableCollection<ProjectInfo> Projects { get; } = [];

    public ObservableCollection<PublishProfile> Profiles { get; } = [];

    /// <summary>最近选择的项目 / 解决方案，最近的在前。</summary>
    public ObservableCollection<RecentProject> RecentProjects { get; } = [];

    public ObservableCollection<PropertyRow> ExtraProperties { get; } = [];

    public ObservableCollection<string> TargetFrameworks { get; } = [];

    public IReadOnlyList<string> RuntimeIdentifiers { get; } = KnownRuntimeIdentifiers;

    public IReadOnlyList<string> Configurations { get; } = ["Debug", "Release"];

    /// <summary>运行时依赖：两个选项都显式列出来，避免只看「自包含」复选框猜反。</summary>
    public IReadOnlyList<DeploymentModeOption> DeploymentModes { get; } =
    [
        new("框架依赖（目标机需自备 .NET 运行时）", false),
        new("自包含（产物自带运行时，体积大）", true),
    ];

    public IReadOnlyList<ZipNamingOption> ZipNamingModes { get; } =
    [
        new("项目名", ZipNamingMode.ProjectName),
        new("项目名_版本号", ZipNamingMode.ProjectNameVersion),
        new("项目名_日期", ZipNamingMode.ProjectNameDate),
        new("自定义", ZipNamingMode.Custom),
    ];

    /// <summary>安装包命名：默认带版本号，方便同时存在多个版本的安装包。</summary>
    public IReadOnlyList<ZipNamingOption> MsiNamingModes { get; } =
    [
        new("产品名", ZipNamingMode.ProjectName),
        new("产品名_版本号", ZipNamingMode.ProjectNameVersion),
        new("自定义", ZipNamingMode.Custom),
    ];

    public AsyncRelayCommand BrowseProjectCommand { get; }

    public AsyncRelayCommand RescanCommand { get; }

    public RelayCommand BrowseOutputRootCommand { get; }

    public RelayCommand ClearLogCommand { get; }

    public RelayCommand ClearRecentProjectsCommand { get; }

    public RelayCommand SaveProfileCommand { get; }

    /// <summary>标题栏文案：带版本号，方便一眼确认手上装的是哪个版本（发版排查也用得上）。</summary>
    public string WindowTitle { get; } = BuildWindowTitle();

    private static string BuildWindowTitle()
    {
        var assembly = typeof(MainViewModel).Assembly;
        var version = (assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute)?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "未知";
        var plus = version.IndexOf('+');
        if (plus >= 0)
        {
            version = version[..plus];
        }

        return "Dotnet 发布工具 " + version;
    }

    public RelayCommand OpenProjectFolderCommand { get; }

    public RelayCommand OpenOutputFolderCommand { get; }

    public RelayCommand CancelCommand { get; }

    public AsyncRelayCommand PublishCommand { get; }

    public string ProjectPath
    {
        get => _projectPath;
        private set => SetProperty(ref _projectPath, value);
    }

    public ProjectInfo? SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (!SetProperty(ref _selectedProject, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ProjectSummary));
            OnPropertyChanged(nameof(ProjectVersion));
            OnPropertyChanged(nameof(PublishDirectoryPreview));
            OnPropertyChanged(nameof(ZipPathPreview));
            OnPropertyChanged(nameof(MsiPathPreview));
            OnPropertyChanged(nameof(MsiVersionPreview));

            // 项目被「置空」只可能是列表刷新 / 绑定回写造成的中间态（发布后的版本刷新会替换项目列表项，
            // 列表的绑定随即把 SelectedProject 回写成 null）。这种情况**什么都不能做**：
            // 曾经在这里清空 TargetFrameworks（目标框架下拉的**选项**）并清掉发布配置列表，而刷新收尾是用
            // 字段直接赋值的、不会走回 setter 填回来 —— 于是"选项消失"。
            // 2026-10-16 由 NLog 日志依次定位到「配置选择丢失」「目标框架选项丢失」，根因都是这一处。
            if (value is null)
            {
                AppendLog("[诊断] 项目选择被清空（中间态），忽略：不动目标框架选项、配置列表与常用设置。");
                RaiseCommandStates();
                return;
            }

            // 同一个项目只是「重新探查」回来（例如版本递增后刷新 ProjectInfo）时不要重载发布配置：
            // 重载会先把常用设置清成兜底值，而配置下拉框在集合刷新后还会把「空选择」回写进 SelectedProfile，
            // 最终把用户填的运行时标识符 / 目标框架悄悄清掉（2026-10-16 定位：第二次打包 MSI 从 60MB 变 212MB）。
            TargetFrameworks.Clear();
            foreach (var framework in value.TargetFrameworks)
            {
                TargetFrameworks.Add(framework);
            }

            AppendLog("[诊断] 项目选择变更：" + value.Path);
            if (!string.Equals(_profilesLoadedFor, value.Path, StringComparison.OrdinalIgnoreCase))
            {
                _profilesLoadedFor = value.Path;
                LoadProfilesFor(value);
            }

            RaiseCommandStates();
        }
    }

    /// <summary>下拉里选中的最近项目；选中即载入。</summary>
    public RecentProject? SelectedRecentProject
    {
        get => _selectedRecentProject;
        set
        {
            if (!SetProperty(ref _selectedRecentProject, value))
            {
                return;
            }

            if (value is null || string.IsNullOrWhiteSpace(value.FilePath))
            {
                return;
            }

            if (!File.Exists(value.FilePath))
            {
                _dialogs.ShowError(
                    "最近的项目 / 解决方案已不存在：" + Environment.NewLine + value.FilePath + Environment.NewLine
                    + "已从记录中移除。",
                    "记录已失效");
                RemoveRecent(value);
                return;
            }

            _ = LoadPathAsync(value.FilePath);
        }
    }

    public bool HasRecentProjects => RecentProjects.Count > 0;

    public PublishProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (value is null && Profiles.Count > 0)
            {
                // 集合刷新（重新扫描 / 重载配置）后下拉框会把「空选择」回写进来。这不是用户的选择：
                // 放行会把常用设置清成兜底值，打包内容随之悄悄改变 —— 直接忽略。
                return;
            }

            if (!SetProperty(ref _selectedProfile, value))
            {
                return;
            }

            ApplyProfile(value);
        }
    }

    public string ProjectSummary => SelectedProject?.Summary ?? "（未选择项目）";

    public string ProjectVersion => SelectedProject?.Version ?? "未解析";

    public string OutputRoot
    {
        get => _outputRoot;
        set
        {
            if (SetProperty(ref _outputRoot, value))
            {
                RebuildPreview();
            }
        }
    }

    public string Configuration
    {
        get => _configuration;
        set
        {
            if (SetProperty(ref _configuration, value))
            {
                RebuildPreview();
            }
        }
    }

    public string? TargetFramework
    {
        get => _targetFramework;
        set
        {
            if (SetProperty(ref _targetFramework, value))
            {
                RebuildPreview();
            }
        }
    }

    public string? RuntimeIdentifier
    {
        get => _runtimeIdentifier;
        set
        {
            if (SetProperty(ref _runtimeIdentifier, value))
            {
                RebuildPreview();
            }
        }
    }

    public bool SelfContained
    {
        get => _selfContained;
        set
        {
            if (SetProperty(ref _selfContained, value))
            {
                RebuildPreview();
            }
        }
    }

    public bool PublishSingleFile
    {
        get => _publishSingleFile;
        set
        {
            if (SetProperty(ref _publishSingleFile, value))
            {
                RebuildPreview();
            }
        }
    }

    public bool PublishReadyToRun
    {
        get => _publishReadyToRun;
        set
        {
            if (SetProperty(ref _publishReadyToRun, value))
            {
                RebuildPreview();
            }
        }
    }

    public bool PublishTrimmed
    {
        get => _publishTrimmed;
        set
        {
            if (SetProperty(ref _publishTrimmed, value))
            {
                RebuildPreview();
            }
        }
    }

    public string EnvironmentName
    {
        get => _environmentName;
        set
        {
            if (SetProperty(ref _environmentName, value))
            {
                RebuildPreview();
            }
        }
    }

    public string VersionOverride
    {
        get => _versionOverride;
        set
        {
            if (SetProperty(ref _versionOverride, value))
            {
                RebuildPreview();
            }
        }
    }

    public bool CleanPublishDirectory
    {
        get => _cleanPublishDirectory;
        set => SetProperty(ref _cleanPublishDirectory, value);
    }

    public bool NoRestore
    {
        get => _noRestore;
        set => SetProperty(ref _noRestore, value);
    }

    public bool MakeZip
    {
        get => _makeZip;
        set
        {
            if (SetProperty(ref _makeZip, value))
            {
                RebuildPreview();
            }
        }
    }

    public ZipNamingMode ZipNamingMode
    {
        get => _zipNamingMode;
        set
        {
            if (SetProperty(ref _zipNamingMode, value))
            {
                OnPropertyChanged(nameof(IsDateNaming));
                OnPropertyChanged(nameof(IsCustomNaming));
                RebuildPreview();
            }
        }
    }

    public string ZipDateFormat
    {
        get => _zipDateFormat;
        set
        {
            if (SetProperty(ref _zipDateFormat, value))
            {
                RebuildPreview();
            }
        }
    }

    public string CustomZipName
    {
        get => _customZipName;
        set
        {
            if (SetProperty(ref _customZipName, value))
            {
                RebuildPreview();
            }
        }
    }

    public bool ZipIncludeRootFolder
    {
        get => _zipIncludeRootFolder;
        set => SetProperty(ref _zipIncludeRootFolder, value);
    }

    /// <summary>发布成功后是否生成 MSI 安装包（装到 Program Files）。</summary>
    public bool MakeMsi
    {
        get => _makeMsi;
        set
        {
            if (SetProperty(ref _makeMsi, value))
            {
                RebuildPreview();
            }
        }
    }

    public string MsiProductName
    {
        get => _msiProductName;
        set
        {
            if (SetProperty(ref _msiProductName, value))
            {
                RebuildPreview();
            }
        }
    }

    public string MsiManufacturer
    {
        get => _msiManufacturer;
        set => SetProperty(ref _msiManufacturer, value);
    }

    public bool MsiCreateShortcut
    {
        get => _msiCreateShortcut;
        set => SetProperty(ref _msiCreateShortcut, value);
    }

    public bool MsiCreateDesktopShortcut
    {
        get => _msiCreateDesktopShortcut;
        set => SetProperty(ref _msiCreateDesktopShortcut, value);
    }

    public ZipNamingMode MsiNamingMode
    {
        get => _msiNamingMode;
        set
        {
            if (SetProperty(ref _msiNamingMode, value))
            {
                OnPropertyChanged(nameof(IsMsiCustomNaming));
                RebuildPreview();
            }
        }
    }

    public string MsiCustomName
    {
        get => _msiCustomName;
        set
        {
            if (SetProperty(ref _msiCustomName, value))
            {
                RebuildPreview();
            }
        }
    }

    public bool IsMsiCustomNaming => MsiNamingMode == ZipNamingMode.Custom;

    /// <summary>
    /// 仅本次递增版本：只对接下来这一次打包生效，**打包成功后自动取消**，也不写进设置（重启即复位）。
    /// 适合「这次想发个新版本，但不想以后每次都自动升」的场景。
    /// </summary>
    public bool MsiAutoBumpVersionOnce
    {
        get => _msiAutoBumpVersionOnce;
        set
        {
            if (SetProperty(ref _msiAutoBumpVersionOnce, value))
            {
                OnPropertyChanged(nameof(IsAutoBumpEnabled));
                RebuildPreview();
            }
        }
    }

    /// <summary>这次打包到底要不要递增：常驻开关或「仅本次」任一勾上即可。</summary>
    public bool IsAutoBumpEnabled => MsiAutoBumpVersion || MsiAutoBumpVersionOnce;

    /// <summary>打包 MSI 时自动把版本递增一位。</summary>
    public bool MsiAutoBumpVersion
    {
        get => _msiAutoBumpVersion;
        set
        {
            if (SetProperty(ref _msiAutoBumpVersion, value))
            {
                OnPropertyChanged(nameof(IsAutoBumpEnabled));
                RebuildPreview();
            }
        }
    }

    /// <summary>自动递增后把新版本写回项目 csproj（本工具唯一会改动源文件的选项）。</summary>
    public bool MsiWriteVersionBack
    {
        get => _msiWriteVersionBack;
        set
        {
            if (SetProperty(ref _msiWriteVersionBack, value))
            {
                RebuildPreview();
            }
        }
    }

    /// <summary>一句话说明这次会发布成什么形态（框架依赖/自包含 + 运行时 + 其它开关）。</summary>
    public string PublishModeSummary
    {
        get
        {
            var mode = SelfContained ? "自包含" : "框架依赖";
            var rid = string.IsNullOrWhiteSpace(RuntimeIdentifier) ? "未指定运行时" : RuntimeIdentifier!.Trim();

            var extras = new List<string>();
            if (PublishSingleFile)
            {
                extras.Add("单文件");
            }

            if (PublishReadyToRun)
            {
                extras.Add("ReadyToRun");
            }

            if (PublishTrimmed)
            {
                extras.Add("裁剪");
            }

            var suffix = extras.Count == 0 ? string.Empty : " · " + string.Join(" + ", extras);
            return $"发布方式：{mode} · {rid}{suffix}";
        }
    }

    public bool IsDateNaming => ZipNamingMode == ZipNamingMode.ProjectNameDate;

    public bool IsCustomNaming => ZipNamingMode == ZipNamingMode.Custom;

    public string PublishDirectoryPreview => SelectedProject is null
        ? "（未选择项目）"
        : PathLayout.GetPublishDirectory(OutputRoot, SelectedProject.Name);

    public string ZipPathPreview
    {
        get
        {
            if (SelectedProject is null)
            {
                return "（未选择项目）";
            }

            if (!MakeZip)
            {
                return "（未启用）";
            }

            var zip = new ZipOptions
            {
                NamingMode = ZipNamingMode,
                DateFormat = string.IsNullOrWhiteSpace(ZipDateFormat) ? ZipOptions.DefaultDateFormat : ZipDateFormat.Trim(),
                CustomName = NullIfEmpty(CustomZipName),
                Version = NullIfEmpty(VersionOverride) ?? SelectedProject.Version,
            };

            var fileName = ZipNaming.Resolve(zip, SelectedProject.Name);
            return Path.Combine(PathLayout.GetProjectRoot(OutputRoot, SelectedProject.Name), fileName);
        }
    }

    /// <summary>安装包路径预览（产品名留空时按项目名，版本按 MSI 规则规整）。</summary>
    public string MsiPathPreview
    {
        get
        {
            if (SelectedProject is null)
            {
                return "（未选择项目）";
            }

            if (!MakeMsi)
            {
                return "（未启用）";
            }

            var productName = string.IsNullOrWhiteSpace(MsiProductName) ? SelectedProject.Name : MsiProductName.Trim();
            var version = MsiVersion.Normalize(
                ResolveMsiVersion(out _, out _) ?? NullIfEmpty(VersionOverride) ?? SelectedProject.Version);
            var fileName = ZipNaming.Resolve(
                MsiNamingMode,
                ZipOptions.DefaultDateFormat,
                NullIfEmpty(MsiCustomName),
                version,
                productName,
                ".msi");

            return Path.Combine(PathLayout.GetProjectRoot(OutputRoot, SelectedProject.Name), fileName);
        }
    }

    /// <summary>本次 MSI 会用到的版本（启用自动递增时是递增后的版本）。</summary>
    public string MsiVersionPreview
    {
        get
        {
            if (SelectedProject is null)
            {
                return "本次版本：（未选择项目）";
            }

            if (!MakeMsi)
            {
                return "本次版本：（未启用 MSI）";
            }

            var version = ResolveMsiVersion(out var baseVersion, out var reason);
            if (version is null)
            {
                return "本次版本：无法确定 —— " + (reason ?? "项目里没有版本号");
            }

            if (!IsAutoBumpEnabled)
            {
                return "本次版本：" + version + "（未启用自动递增）";
            }

            var where = MsiWriteVersionBack ? "并写回项目 csproj" : "只用于本次打包";
            var scope = MsiAutoBumpVersionOnce && !MsiAutoBumpVersion ? "仅本次，" : string.Empty;
            return "本次版本：" + version + "（自动递增，" + scope + "基准 " + (baseVersion ?? "未知") + "，" + where + "）";
        }
    }

    /// <summary>本次 MSI 要用的版本号；启用自动递增时返回递增后的值。</summary>
    private string? ResolveMsiVersion(out string? baseVersion, out string? reason)
    {
        baseVersion = null;
        reason = null;
        var project = SelectedProject;
        if (project is null)
        {
            return null;
        }

        var projectVersion = NullIfEmpty(VersionOverride) ?? project.Version;
        if (!IsAutoBumpEnabled)
        {
            baseVersion = projectVersion;
            return projectVersion;
        }

        try
        {
            var plan = _versionService.Resolve(new MsiOptions { AutoBumpVersion = true }, project.Path, projectVersion);
            baseVersion = plan?.BaseVersion;
            return plan?.NextVersion;
        }
        catch (PublishException ex)
        {
            reason = ex.Message;
            return null;
        }
    }

    public string ProfilePreviewXml
    {
        get => _profilePreviewXml;
        private set => SetProperty(ref _profilePreviewXml, value);
    }

    public string LogText
    {
        get => _logText;
        private set => SetProperty(ref _logText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommandStates();
            }
        }
    }

    /// <summary>从设置里恢复上次的输出目录与打包选项。</summary>
    public void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        var settings = _settingsService.Load();

        OutputRoot = string.IsNullOrWhiteSpace(settings.OutputRoot) ? PathLayout.DefaultOutputRoot : settings.OutputRoot!;
        MakeZip = settings.MakeZipAfterPublish;
        ZipNamingMode = settings.ZipNamingMode;
        ZipDateFormat = string.IsNullOrWhiteSpace(settings.ZipDateFormat) ? ZipOptions.DefaultDateFormat : settings.ZipDateFormat;
        ZipIncludeRootFolder = settings.ZipIncludeRootFolder;
        MakeMsi = settings.MakeMsiAfterPublish;
        MsiProductName = settings.MsiProductName ?? string.Empty;
        MsiManufacturer = settings.MsiManufacturer ?? string.Empty;
        MsiCreateShortcut = settings.MsiCreateShortcut;
        MsiCreateDesktopShortcut = settings.MsiCreateDesktopShortcut;
        MsiNamingMode = settings.MsiNamingMode;
        MsiCustomName = settings.MsiCustomName ?? string.Empty;
        MsiAutoBumpVersion = settings.MsiAutoBumpVersion;
        MsiWriteVersionBack = settings.MsiWriteVersionBack;
        _lastProjectPath = settings.LastProjectPath;

        RecentProjects.Clear();
        foreach (var entry in settings.RecentProjects.Where(e => !string.IsNullOrWhiteSpace(e.FilePath)))
        {
            RecentProjects.Add(entry);
        }

        OnPropertyChanged(nameof(HasRecentProjects));
        ClearRecentProjectsCommand.RaiseCanExecuteChanged();

        var integrity = ProcessIntegrity.GetCurrent();
        AppendLog("[环境] 工具版本：" + WindowTitle + "；进程完整性级别：" + ProcessIntegrity.Describe(integrity));

        // 日志文件位置（NLog）：排查现场问题时让用户一眼知道去哪拿日志；写不进去也在这里说清楚。
        if (App.LogProblem is null)
        {
            AppendLog("[环境] 工具日志：" + Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DotnetPublishTool", "logs"));
        }
        else
        {
            AppendLog("[提示] " + App.LogProblem);
        }
        if (ProcessIntegrity.IsBelowMedium)
        {
            AppendLog("[环境] 低完整性进程写不了「文档」「%APPDATA%」等目录。");
            AppendLog("[环境] 若产物被沙箱打过 Low 标签，请在普通终端运行 tools/fix-build-integrity.ps1 后用修复过的 exe 启动。");
        }
    }

    /// <summary>窗口首次显示时调用，自动恢复上次的项目。</summary>
    public void OnWindowLoaded()
    {
        // 优先恢复最近一次还能找到的项目；都没有则退回上次记录。
        var candidate = RecentProjects.FirstOrDefault(r => File.Exists(r.FilePath))?.FilePath
            ?? _lastProjectPath;

        if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
        {
            _ = LoadPathAsync(candidate!);
        }
    }

    /// <summary>加载项目文件或解决方案文件。</summary>
    public async Task LoadPathAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = "正在解析项目…";
            // 先解析、成功后再替换界面状态：解析中途失败（例如项目文件被占用）时，已有的项目与
            // 发布配置必须原样保留。以前是「先清空再解析」，一旦这一步抛异常，项目和配置列表会一起
            // 空掉且再也回不来（2026-10-16 实测：发布后重新扫描，FolderProfile 直接消失）。
            ProjectInfo? primary = null;
            var loaded = new List<ProjectInfo>();
            string? loadedPath = null;
            string readyStatus;

            if (ProjectInspector.IsSolutionFile(path))
            {
                var projectPaths = SolutionParser.GetProjectPaths(path);
                if (projectPaths.Count == 0)
                {
                    _dialogs.ShowError("该解决方案中没有找到可发布的 .csproj / .vbproj / .fsproj 项目文件。");
                    StatusText = "解决方案中未找到项目。";
                    return;
                }

                loadedPath = Path.GetFullPath(path);
                foreach (var projectPath in projectPaths)
                {
                    loaded.Add(await _inspector.InspectAsync(projectPath, loadedPath).ConfigureAwait(true));
                }

                primary = loaded.FirstOrDefault();
                readyStatus = $"已从解决方案载入 {loaded.Count} 个项目。";
            }
            else if (ProjectInspector.IsProjectFile(path))
            {
                var project = await _inspector.InspectAsync(path).ConfigureAwait(true);
                loadedPath = project.Path;
                loaded.Add(project);
                primary = project;
                readyStatus = "已载入项目：" + project.Name;
            }
            else
            {
                _dialogs.ShowError("请选择 .csproj / .vbproj / .fsproj 项目文件，或 .sln / .slnx 解决方案文件。");
                StatusText = "文件类型不支持。";
                return;
            }

            // 解析成功，才一次性替换界面状态（先置空再选上：置空会让发布配置按新项目重新载入）
            Projects.Clear();
            Profiles.Clear();
            ExtraProperties.Clear();
            SelectedProject = null;
            SelectedProfile = null;
            foreach (var item in loaded)
            {
                Projects.Add(item);
            }

            ProjectPath = loadedPath!;
            _profilesLoadedFor = null;   // 显式载入（含重新扫描）要强制重载配置，同一项目也不例外
            SelectedProject = primary;
            StatusText = readyStatus;
            RecordRecent(loadedPath!);
        }
        catch (Exception ex)
        {
            AppendLog("[错误] " + ex.Message);
            _dialogs.ShowError(ex.Message, "载入失败");
            StatusText = "载入失败。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>把当前生效的发布配置副本保存到默认目录（不发布）。</summary>
    public string? SaveProfileOnly()
    {
        if (SelectedProject is null)
        {
            _dialogs.ShowError("请先选择一个项目。");
            return null;
        }

        if (!EnsureOutputRootUsable())
        {
            return null;
        }

        var request = BuildRequest(forPackaging: false);
        if (request is null)
        {
            _dialogs.ShowError("请先选择一个项目。");
            return null;
        }

        try
        {
            var saved = _workflow.SaveProfile(request);
            AppendLog("[配置] 已保存配置副本：" + saved.ProfilePath);
            StatusText = "已保存配置副本。";
            PersistSettings();
            return saved.ProfilePath;
        }
        catch (Exception ex)
        {
            AppendLog("[错误] " + ex.Message);
            _dialogs.ShowError(ex.Message, "保存配置失败");
            return null;
        }
    }

    /// <summary>生成并发布（按需打包）。</summary>
    public async Task<PublishResult?> PublishAsync()
    {
        if (SelectedProject is null)
        {
            _dialogs.ShowError("请先选择一个项目。");
            return null;
        }

        // 必须在 BuildRequest 之前：不可写时这里会把 OutputRoot 换成可写目录。
        if (!EnsureOutputRootUsable())
        {
            return null;
        }

        WarnIfProjectDirectoryNotWritable();

        var request = BuildRequest(forPackaging: true);
        if (request is null)
        {
            _dialogs.ShowError("请先选择一个项目。");
            return null;
        }

        if (!request.Project.IsSdkStyle)
        {
            _dialogs.ShowError("该项目不是 SDK 风格项目，dotnet publish 无法发布。");
            return null;
        }

        if (request.Project.TargetFrameworks.Count > 1 && string.IsNullOrWhiteSpace(request.Options.TargetFramework))
        {
            _dialogs.ShowError("该项目包含多个目标框架，请先在「目标框架」中选择一个。");
            return null;
        }

        _cancellation = new CancellationTokenSource();
        IsBusy = true;
        StatusText = "正在发布…";
        AppendLog(string.Empty);
        AppendLog("==================== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ====================");

        try
        {
            // 用 lambda 包一层：AppendLog 带 [CallerFilePath]/[CallerLineNumber] 可选参数，方法组无法直接转成 Action<string>。
            var progress = new Progress<string>(line => AppendLog(line));
            var result = await _workflow.RunAsync(request, progress, _cancellation.Token).ConfigureAwait(true);

            if (result.Success)
            {
                var extras = new List<string>();
                if (result.ZipPath is not null)
                {
                    extras.Add("已生成压缩包");
                }

                if (result.MsiPath is not null)
                {
                    extras.Add("已生成 MSI 安装包");
                }

                var extra = extras.Count == 0 ? string.Empty : "，" + string.Join("、", extras);
                StatusText = $"发布成功：{result.FileCount} 个文件，用时 {result.Duration.TotalSeconds:F1}s{extra}。";
                PersistSettings();

                if (result.BumpedVersion is not null)
                {
                    StatusText += " 本次版本：" + result.BumpedVersion + "。";
                    if (MsiAutoBumpVersionOnce)
                    {
                        // 「仅本次递增」用完即关：避免下次打包又悄悄升一版。
                        MsiAutoBumpVersionOnce = false;
                        AppendLog("[MSI] 仅本次递增已生效，选项已自动取消。");
                    }

                    // 写回只改了磁盘上的 csproj：这里定点刷新一下界面上显示的项目版本。
                    await RefreshSelectedProjectAsync().ConfigureAwait(true);
                }

                if (result.VersionError is not null)
                {
                    AppendLog("[版本] 写回/记录失败：" + result.VersionError);
                    _dialogs.ShowError(
                        "安装包已经生成，但新版本没能写回项目：" + Environment.NewLine + Environment.NewLine
                        + result.VersionError,
                        "版本写回失败");
                }

                if (result.MsiError is not null)
                {
                    _dialogs.ShowError(
                        "发布已完成，但 MSI 安装包没有生成成功：" + Environment.NewLine + Environment.NewLine
                        + result.MsiError + Environment.NewLine + Environment.NewLine
                        + "发布产物仍在发布目录里，可在普通终端重跑一次打包。",
                        "MSI 生成失败");
                }
            }
            else
            {
                StatusText = "发布失败。";
                _dialogs.ShowError(result.ErrorMessage ?? "发布失败。", "发布失败");
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            AppendLog("[取消] 已取消发布。");
            StatusText = "已取消。";
            return null;
        }
        catch (Exception ex)
        {
            AppendLog("[异常] " + ex);
            _dialogs.ShowError(ex.Message, "发布异常");
            StatusText = "发布异常。";
            return null;
        }
        finally
        {
            IsBusy = false;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    /// <summary>
    /// 发布成功后重新读取项目版本（自动递增写回 csproj、或版本记录推进之后，界面上的版本会变）。
    /// 只替换 ProjectInfo、不重新载入发布配置 —— 走 SelectedProject 的 setter 会把界面上改过的
    /// 常用设置与「其他属性」冲掉。
    /// </summary>
    private async Task RefreshSelectedProjectAsync()
    {
        var project = SelectedProject;
        if (project is null)
        {
            return;
        }

        ProjectInfo refreshed;
        try
        {
            refreshed = await _inspector.InspectAsync(project.Path, project.SolutionPath).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppendLog("[版本] 重新读取项目版本失败：" + ex.Message);
            return;
        }

        if (string.Equals(project.Version, refreshed.Version, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var index = Projects.IndexOf(project);
        if (index >= 0)
        {
            Projects[index] = refreshed;
        }

        _selectedProject = refreshed;
        OnPropertyChanged(nameof(SelectedProject));
        OnPropertyChanged(nameof(ProjectSummary));
        OnPropertyChanged(nameof(ProjectVersion));
        OnPropertyChanged(nameof(PublishDirectoryPreview));
        OnPropertyChanged(nameof(ZipPathPreview));
        OnPropertyChanged(nameof(MsiPathPreview));
        OnPropertyChanged(nameof(MsiVersionPreview));
        OnPropertyChanged(nameof(PublishModeSummary));

        AppendLog("[版本] 界面已刷新项目版本：" + (refreshed.Version ?? "未解析"));
    }

    private async Task BrowseProjectAsync()
    {
        var path = _dialogs.PickProjectFile();
        if (!string.IsNullOrWhiteSpace(path))
        {
            await LoadPathAsync(path!);
        }
    }

    private async Task RescanAsync()
    {
        if (!string.IsNullOrWhiteSpace(ProjectPath))
        {
            await LoadPathAsync(ProjectPath);
        }
    }

    private void BrowseOutputRoot()
    {
        var picked = _dialogs.PickFolder(OutputRoot);
        if (!string.IsNullOrWhiteSpace(picked))
        {
            OutputRoot = picked!;
        }
    }

    private void ClearLog()
    {
        lock (_logLock)
        {
            _logBuilder.Clear();
        }

        LogText = string.Empty;
    }

    private void Cancel() => _cancellation?.Cancel();

    /// <summary>在资源管理器里打开当前项目（.csproj）所在目录。</summary>
    private void OpenProjectFolder()
    {
        var projectPath = SelectedProject?.Path ?? ProjectPath;
        var directory = string.IsNullOrWhiteSpace(projectPath) ? null : Path.GetDirectoryName(projectPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            _dialogs.ShowInfo("找不到项目所在目录：" + (string.IsNullOrEmpty(directory) ? projectPath ?? "（没有选中项目）" : directory));
            return;
        }

        Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
    }

    private void OpenOutputFolder()
    {
        var directory = SelectedProject is null
            ? OutputRoot
            : PathLayout.GetProjectRoot(OutputRoot, SelectedProject.Name);

        if (!Directory.Exists(directory))
        {
            _dialogs.ShowInfo("输出目录还不存在：" + directory);
            return;
        }

        Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
    }

    private void LoadProfilesFor(ProjectInfo? project)
    {
        var previousProfileName = _selectedProfile?.Name;
        AppendLog("[诊断] 重新载入发布配置（项目：" + (project?.Name ?? "无") + "，原选择：" + (previousProfileName ?? "无") + "）。");
        Profiles.Clear();

        if (project is null)
        {
            // 项目暂时为空（列表刷新的中间态）：配置列表与常用设置**都保持不动**。
            // 真正换项目时，新项目的 LoadProfilesFor(project) 会清空并重新填充它们。
            AppendLog("[诊断] 重新载入发布配置被跳过（项目为空）。");
            return;
        }

        foreach (var profile in _profileStore.Discover(project))
        {
            Profiles.Add(profile);
        }

        if (!string.IsNullOrWhiteSpace(project.EvaluationWarning))
        {
            AppendLog("[提示] " + project.EvaluationWarning);
        }

        SelectedProfile = Profiles.FirstOrDefault(p => string.Equals(p.Name, previousProfileName, StringComparison.OrdinalIgnoreCase))
            ?? Profiles.FirstOrDefault();
    }

    private void ApplyProfile(PublishProfile? profile)
    {
        var project = SelectedProject;
        var previousRuntime = _runtimeIdentifier;

        // 空配置**一律**只保留现有常用设置，绝不清理它们。项目列表/配置列表刷新过程中会短暂出现
        // 「项目为空」或「配置为空」的中间态，而这些中间态没有语义 —— 以前把它们当成「清空设置」，
        // 结果运行时标识符 / 目标框架被静默清成兜底值，打包内容随之改变（2026-10-16 两次踩到：
        // 一次是列表刷新期间的空选择，一次是发布成功后项目瞬时空掉）。
        // 现在只有「真正应用某个配置」才会写这些字段；换项目时新项目的配置会覆盖它们。
        if (profile is null)
        {
            AppendLog("[诊断] 收到空配置，已保留当前常用设置（不清空）。");
            RebuildPreview();
            return;
        }

        _configuration = profile?.GetValue("Configuration") ?? "Release";
        _targetFramework = profile?.GetValue("TargetFramework")
            ?? (project?.TargetFrameworks.Count == 1 ? project.TargetFrameworks[0] : null);
        _runtimeIdentifier = profile?.GetValue("RuntimeIdentifier");
        _selfContained = profile?.GetBool("SelfContained") ?? false;
        _publishSingleFile = profile?.GetBool("PublishSingleFile") ?? false;
        _publishReadyToRun = profile?.GetBool("PublishReadyToRun") ?? false;
        _publishTrimmed = profile?.GetBool("PublishTrimmed") ?? false;
        _environmentName = profile?.GetValue("EnvironmentName") ?? string.Empty;
        _versionOverride = profile?.GetValue("Version") ?? string.Empty;

        ExtraProperties.Clear();
        if (profile is not null)
        {
            foreach (var property in profile.Properties.Where(p => !PublishOptionKeys.IsCurated(p.Key)))
            {
                ExtraProperties.Add(new PropertyRow(property.Key, property.Value));
            }
        }

        OnPropertyChanged(nameof(Configuration));
        OnPropertyChanged(nameof(TargetFramework));
        OnPropertyChanged(nameof(RuntimeIdentifier));
        OnPropertyChanged(nameof(SelfContained));
        OnPropertyChanged(nameof(PublishSingleFile));
        OnPropertyChanged(nameof(PublishReadyToRun));
        OnPropertyChanged(nameof(PublishTrimmed));
        OnPropertyChanged(nameof(EnvironmentName));
        OnPropertyChanged(nameof(VersionOverride));

        if (!string.IsNullOrWhiteSpace(previousRuntime) && string.IsNullOrWhiteSpace(_runtimeIdentifier))
        {
            AppendLog("[提示] 运行时标识符被重置为空（发布配置没有生效）：这次发布会与平台无关，"
                + "各平台原生库可能被打进安装包、体积大好几倍，请到「常用设置 → 运行时标识符」填 win-x64。");
        }

        RebuildPreview();
    }

    private PublishRequest? BuildRequest(bool forPackaging)
    {
        var project = SelectedProject;
        if (project is null)
        {
            return null;
        }

        var options = new PublishOptions
        {
            Configuration = string.IsNullOrWhiteSpace(Configuration) ? "Release" : Configuration.Trim(),
            TargetFramework = NullIfEmpty(TargetFramework),
            RuntimeIdentifier = NullIfEmpty(RuntimeIdentifier),
            SelfContained = SelfContained,
            PublishSingleFile = PublishSingleFile,
            PublishReadyToRun = PublishReadyToRun,
            PublishTrimmed = PublishTrimmed,
            EnvironmentName = NullIfEmpty(EnvironmentName),
            VersionOverride = NullIfEmpty(VersionOverride),
            CleanPublishDirectory = CleanPublishDirectory,
            NoRestore = NoRestore,
        };

        var zip = new ZipOptions
        {
            Enabled = forPackaging && MakeZip,
            NamingMode = ZipNamingMode,
            DateFormat = string.IsNullOrWhiteSpace(ZipDateFormat) ? ZipOptions.DefaultDateFormat : ZipDateFormat.Trim(),
            IncludeRootFolder = ZipIncludeRootFolder,
            CustomName = NullIfEmpty(CustomZipName),
            Version = options.VersionOverride ?? project.Version,
        };

        var msi = new MsiOptions
        {
            Enabled = forPackaging && MakeMsi,
            ProductName = NullIfEmpty(MsiProductName),
            Manufacturer = NullIfEmpty(MsiManufacturer),
            CreateShortcut = MsiCreateShortcut,
            CreateDesktopShortcut = MsiCreateDesktopShortcut,
            AutoBumpVersion = IsAutoBumpEnabled,
            WriteVersionBack = MsiWriteVersionBack,
            NamingMode = MsiNamingMode,
            CustomName = NullIfEmpty(MsiCustomName),
            Version = options.VersionOverride ?? project.Version,
        };

        return new PublishRequest
        {
            Project = project,
            Options = options,
            OutputRoot = string.IsNullOrWhiteSpace(OutputRoot) ? PathLayout.DefaultOutputRoot : OutputRoot.Trim(),
            ProfileName = SelectedProfile?.Name ?? PublishProfileStore.DefaultProfileName,
            ExtraProperties = ExtraProperties
                .Where(row => !string.IsNullOrWhiteSpace(row.Key))
                .Select(row => row.ToProperty())
                .ToList(),
            SourceItemGroupsXml = SelectedProfile?.ItemGroupsXml,
            Zip = zip,
            Msi = msi,
        };
    }

    private void RebuildPreview()
    {
        var request = BuildRequest(forPackaging: false);
        if (request is null)
        {
            ProfilePreviewXml = string.Empty;
        }
        else
        {
            var publishDirectory = PathLayout.GetPublishDirectory(request.OutputRoot, request.Project.Name);
            var properties = PublishWorkflow.BuildEffectiveProperties(request, publishDirectory);
            ProfilePreviewXml = PublishWorkflow.RenderProfileXml(properties);
        }

        OnPropertyChanged(nameof(PublishDirectoryPreview));
        OnPropertyChanged(nameof(ZipPathPreview));
        OnPropertyChanged(nameof(MsiPathPreview));
        OnPropertyChanged(nameof(MsiVersionPreview));
        OnPropertyChanged(nameof(PublishModeSummary));
    }

    private void OnExtraPropertiesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (PropertyRow row in e.NewItems)
            {
                row.PropertyChanged += OnExtraRowChanged;
            }
        }

        if (e.OldItems is not null)
        {
            foreach (PropertyRow row in e.OldItems)
            {
                row.PropertyChanged -= OnExtraRowChanged;
            }
        }

        RebuildPreview();
    }

    private void OnExtraRowChanged(object? sender, PropertyChangedEventArgs e) => RebuildPreview();

    /// <summary>把路径记到「最近项目」列表头部（去重、限长）并立即持久化。</summary>
    private void RecordRecent(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch
        {
            return;
        }

        var existing = RecentProjects.FirstOrDefault(
            r => string.Equals(r.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            RecentProjects.Remove(existing);
        }

        var entry = new RecentProject { FilePath = fullPath, LastUsed = DateTimeOffset.Now };
        RecentProjects.Insert(0, entry);

        while (RecentProjects.Count > MaxRecentProjects)
        {
            RecentProjects.RemoveAt(RecentProjects.Count - 1);
        }

        // 直接写字段，避免触发 setter 再次载入。
        _selectedRecentProject = entry;
        OnPropertyChanged(nameof(SelectedRecentProject));
        OnPropertyChanged(nameof(HasRecentProjects));
        ClearRecentProjectsCommand.RaiseCanExecuteChanged();
        PersistSettings();
    }

    private void RemoveRecent(RecentProject entry)
    {
        RecentProjects.Remove(entry);
        if (ReferenceEquals(_selectedRecentProject, entry))
        {
            _selectedRecentProject = null;
            OnPropertyChanged(nameof(SelectedRecentProject));
        }

        OnPropertyChanged(nameof(HasRecentProjects));
        ClearRecentProjectsCommand.RaiseCanExecuteChanged();
        PersistSettings();
    }

    private void ClearRecentProjects()
    {
        RecentProjects.Clear();
        _selectedRecentProject = null;
        OnPropertyChanged(nameof(SelectedRecentProject));
        OnPropertyChanged(nameof(HasRecentProjects));
        ClearRecentProjectsCommand.RaiseCanExecuteChanged();
        PersistSettings();
        AppendLog("[记录] 已清除最近项目记录。");
    }

    /// <summary>
    /// 用一次真实的写探测确认输出根目录可用；不可用时自动切换到第一个可写候选目录并明确告知。
    /// 这样在「文档」被保护或进程处于受限/沙箱环境时，不会再抛出 UnauthorizedAccessException。
    /// </summary>
    private bool EnsureOutputRootUsable()
    {
        var preferred = string.IsNullOrWhiteSpace(OutputRoot) ? PathLayout.DefaultOutputRoot : OutputRoot.Trim();
        var reason = OutputDirectoryService.TryEnsureWritable(preferred);
        if (reason is null)
        {
            return true;
        }

        var hint = BuildIntegrityHint();
        var reasonText = reason + (hint is null ? string.Empty : Environment.NewLine + hint);

        AppendLog($"[警告] 输出目录不可写：{preferred}");
        AppendLog($"[警告] 原因：{reason}");
        if (hint is not null)
        {
            AppendLog("[警告] " + hint);
        }

        foreach (var candidate in OutputDirectoryService.GetFallbackRoots())
        {
            if (string.Equals(candidate, preferred, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (OutputDirectoryService.TryEnsureWritable(candidate) is not null)
            {
                continue;
            }

            OutputRoot = candidate;
            AppendLog($"[提示] 已自动改用可写目录：{candidate}");
            StatusText = "原输出目录不可写，已改用：" + candidate;
            _dialogs.ShowInfo(
                "输出目录不可写：" + Environment.NewLine + preferred + Environment.NewLine
                + "原因：" + reasonText + Environment.NewLine + Environment.NewLine
                + "已自动改用可写目录：" + Environment.NewLine + candidate,
                "输出目录已自动切换");
            return true;
        }

        var message = "输出目录不可写，也没有找到可用的替代目录。" + Environment.NewLine + Environment.NewLine
            + "目录：" + preferred + Environment.NewLine
            + "原因：" + reasonText;
        AppendLog("[错误] " + message.Replace(Environment.NewLine, " "));
        StatusText = "输出目录不可写。";
        _dialogs.ShowError(message, "输出目录不可写");
        return false;
    }

    /// <summary>进程完整性低于 Medium 时，给出「为什么写不了」以及可执行的修复动作。</summary>
    private static string? BuildIntegrityHint()
    {
        if (!ProcessIntegrity.IsBelowMedium)
        {
            return null;
        }

        return "当前进程完整性级别为 " + ProcessIntegrity.Describe(ProcessIntegrity.GetCurrent())
            + "：低完整性进程只能写 Low 对象，写「文档」「%APPDATA%」这类常规用户目录会被系统拒绝，与 ACL 无关。"
            + "常见原因是构建产物被沙箱/受限环境打上了 Low 标签 —— 请在普通（非沙箱）终端运行 tools/fix-build-integrity.ps1，"
            + "然后用修复过的 exe 重新启动。";
    }

    /// <summary>dotnet publish 需要写项目目录下的 obj/bin；不可写时提前提示，避免只看到 MSBuild 的报错。</summary>
    private void WarnIfProjectDirectoryNotWritable()
    {
        var project = SelectedProject;
        if (project is null)
        {
            return;
        }

        var reason = OutputDirectoryService.TryEnsureWritable(project.Directory);
        if (reason is not null)
        {
            AppendLog($"[警告] 项目目录不可写（{reason}）：{project.Directory}");
            AppendLog("[警告] dotnet publish 需要在该目录下写 obj/bin，发布可能因此失败。");
        }
    }

    private void PersistSettings()
    {
        _settingsService.Save(new AppSettings
        {
            SettingsVersion = AppSettings.CurrentSettingsVersion,
            OutputRoot = OutputRoot,
            MakeZipAfterPublish = MakeZip,
            ZipNamingMode = ZipNamingMode,
            ZipDateFormat = ZipDateFormat,
            ZipIncludeRootFolder = ZipIncludeRootFolder,
            MakeMsiAfterPublish = MakeMsi,
            MsiProductName = NullIfEmpty(MsiProductName),
            MsiManufacturer = NullIfEmpty(MsiManufacturer),
            MsiCreateShortcut = MsiCreateShortcut,
            MsiCreateDesktopShortcut = MsiCreateDesktopShortcut,
            MsiNamingMode = MsiNamingMode,
            MsiCustomName = NullIfEmpty(MsiCustomName),
            MsiAutoBumpVersion = MsiAutoBumpVersion,
            MsiWriteVersionBack = MsiWriteVersionBack,
            LastProjectPath = string.IsNullOrWhiteSpace(ProjectPath) ? _lastProjectPath : ProjectPath,
            RecentProjects = RecentProjects.ToList(),
        });
    }

    private void RaiseCommandStates()
    {
        BrowseProjectCommand.RaiseCanExecuteChanged();
        RescanCommand.RaiseCanExecuteChanged();
        BrowseOutputRootCommand.RaiseCanExecuteChanged();
        SaveProfileCommand.RaiseCanExecuteChanged();
        OpenProjectFolderCommand.RaiseCanExecuteChanged();
        OpenOutputFolderCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        PublishCommand.RaiseCanExecuteChanged();
        CheckUiStateConsistency();
    }

    private bool _warnedEmptyProfiles;
    private bool _warnedEmptyFrameworks;

    /// <summary>
    /// 界面状态自检：已经选中项目时，绑定的列表不该是空的。
    /// 2026-10-16 连着被咬三次（常用设置被清空、发布配置列表被清空、目标框架选项被清空），
    /// 根因都是「瞬时空项目」把集合清了却没人填回来 —— 以后同类问题由这里自己报出来。
    /// </summary>
    private void CheckUiStateConsistency()
    {
        if (SelectedProject is null)
        {
            _warnedEmptyProfiles = false;
            _warnedEmptyFrameworks = false;
            return;
        }

        if (!_warnedEmptyProfiles && Profiles.Count == 0)
        {
            _warnedEmptyProfiles = true;
            AppendLog("[提示] 界面状态异常：已选中项目，但「发布配置」列表为空（可能是刷新过程中被清空且未重新填充）。");
        }

        if (!_warnedEmptyFrameworks && TargetFrameworks.Count == 0 && SelectedProject.TargetFrameworks.Count > 0)
        {
            _warnedEmptyFrameworks = true;
            AppendLog("[提示] 界面状态异常：已选中项目，但「目标框架」下拉没有选项（可能是刷新过程中被清空且未重新填充）。");
        }
    }

    private static readonly NLog.Logger FileLog = NLog.LogManager.GetLogger("UI");

    private void AppendLog(
        string line,
        [System.Runtime.CompilerServices.CallerFilePath] string callerFile = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int callerLine = 0)
    {
        WriteLogFile(line, callerFile, callerLine);

        string snapshot;
        lock (_logLock)
        {
            _logBuilder.AppendLine(line);
            snapshot = _logBuilder.ToString();
        }

        LogText = snapshot;
    }

    /// <summary>
    /// 界面日志同时落盘，并按内容判断等级：错误/失败 → Error，提醒/提示 → Warn，诊断 → Debug，其余 Info。
    /// 想看（或不看）[诊断] 这类细节，改 NLog.config 里的 minlevel 即可，不用改代码。
    /// </summary>
    private static void WriteLogFile(string line, string callerFile, int callerLine)
    {
        var level = NLog.LogLevel.Info;
        if (line.Contains("[错误]", StringComparison.Ordinal) || line.Contains("失败", StringComparison.Ordinal))
        {
            level = NLog.LogLevel.Error;
        }
        else if (line.Contains("[提醒]", StringComparison.Ordinal) || line.Contains("[提示]", StringComparison.Ordinal))
        {
            level = NLog.LogLevel.Warn;
        }
        else if (line.Contains("[诊断]", StringComparison.Ordinal))
        {
            level = NLog.LogLevel.Debug;
        }

        // 把真实调用点带上：否则日志里的"调用点"只会显示到这个写日志的小函数本身，排查时看不出出处。
        var logEvent = new NLog.LogEventInfo(level, FileLog.Name, line);
        if (!string.IsNullOrEmpty(callerFile))
        {
            logEvent.SetCallerInfo(null, null, callerFile, callerLine);
        }

        FileLog.Log(logEvent);
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
