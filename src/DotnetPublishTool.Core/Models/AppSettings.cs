namespace DotnetPublishTool.Core.Models;

/// <summary>跨会话保留的界面设置。</summary>
public sealed class AppSettings
{
    /// <summary>当前设置结构版本；用于让新增的默认值在老配置上生效。</summary>
    public const int CurrentSettingsVersion = 5;

    /// <summary>设置结构版本；0 表示旧版（无版本号）。</summary>
    public int SettingsVersion { get; set; }

    /// <summary>输出根目录；为空时使用默认（文档/DotnetPublishTool）。</summary>
    public string? OutputRoot { get; set; }

    public bool MakeZipAfterPublish { get; set; } = true;

    public ZipNamingMode ZipNamingMode { get; set; } = ZipNamingMode.ProjectNameDate;

    public string ZipDateFormat { get; set; } = ZipOptions.DefaultDateFormat;

    public bool ZipIncludeRootFolder { get; set; }

    /// <summary>发布成功后是否生成 MSI 安装包。</summary>
    public bool MakeMsiAfterPublish { get; set; }

    /// <summary>MSI 产品名（控制面板与开始菜单里的显示名）；为空时用项目名。</summary>
    public string? MsiProductName { get; set; }

    /// <summary>MSI 发布者；为空时用产品名。</summary>
    public string? MsiManufacturer { get; set; }

    /// <summary>MSI 是否创建开始菜单快捷方式。</summary>
    public bool MsiCreateShortcut { get; set; } = true;

    public bool MsiCreateDesktopShortcut { get; set; } = true;

    public ZipNamingMode MsiNamingMode { get; set; } = ZipNamingMode.ProjectNameVersion;

    public string? MsiCustomName { get; set; }

    /// <summary>打包 MSI 时自动递增版本。</summary>
    public bool MsiAutoBumpVersion { get; set; }

    /// <summary>自动递增后把新版本写回项目 csproj（本工具唯一会改动源文件的选项）。</summary>
    public bool MsiWriteVersionBack { get; set; } = true;

    public string? LastProjectPath { get; set; }

    /// <summary>最近选择的项目 / 解决方案，最近的在前。</summary>
    public List<RecentProject> RecentProjects { get; set; } = [];
}
