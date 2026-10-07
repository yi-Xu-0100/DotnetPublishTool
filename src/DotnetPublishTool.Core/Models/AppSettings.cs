namespace DotnetPublishTool.Core.Models;

/// <summary>跨会话保留的界面设置。</summary>
public sealed class AppSettings
{
    /// <summary>当前设置结构版本；用于让新增的默认值在老配置上生效。</summary>
    public const int CurrentSettingsVersion = 3;

    /// <summary>设置结构版本；0 表示旧版（无版本号）。</summary>
    public int SettingsVersion { get; set; }

    /// <summary>输出根目录；为空时使用默认（文档/DotnetPublishTool）。</summary>
    public string? OutputRoot { get; set; }

    public bool MakeZipAfterPublish { get; set; } = true;

    public ZipNamingMode ZipNamingMode { get; set; } = ZipNamingMode.ProjectNameDate;

    public string ZipDateFormat { get; set; } = ZipOptions.DefaultDateFormat;

    public bool ZipIncludeRootFolder { get; set; }

    public string? LastProjectPath { get; set; }

    /// <summary>最近选择的项目 / 解决方案，最近的在前。</summary>
    public List<RecentProject> RecentProjects { get; set; } = [];
}
