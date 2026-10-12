namespace DotnetPublishTool.Core.Models;

/// <summary>发布时可编辑的常用设置。</summary>
public sealed class PublishOptions
{
    /// <summary>生成配置：Debug / Release。</summary>
    public string Configuration { get; set; } = "Release";

    /// <summary>目标框架；多目标项目必填。</summary>
    public string? TargetFramework { get; set; }

    /// <summary>运行时标识符，例如 win-x64；留空表示不指定。</summary>
    public string? RuntimeIdentifier { get; set; }

    public bool SelfContained { get; set; }

    public bool PublishSingleFile { get; set; }

    public bool PublishReadyToRun { get; set; }

    public bool PublishTrimmed { get; set; }

    /// <summary>ASP.NET Core 环境名（EnvironmentName），非 Web 项目可留空。</summary>
    public string? EnvironmentName { get; set; }

    /// <summary>覆盖项目版本号（对应 MSBuild 的 Version）；留空表示使用项目自身。</summary>
    public string? VersionOverride { get; set; }

    /// <summary>发布前清空发布目录，避免残留旧文件。</summary>
    public bool CleanPublishDirectory { get; set; } = true;

    /// <summary>使用 --no-restore（要求已还原）。</summary>
    public bool NoRestore { get; set; }
}

/// <summary>一次发布请求。</summary>
public sealed class PublishRequest
{
    public required ProjectInfo Project { get; init; }

    public required PublishOptions Options { get; init; }

    /// <summary>输出根目录（默认：文档/DotnetPublishTool）。</summary>
    public required string OutputRoot { get; init; }

    /// <summary>发布配置名，用于生成副本文件名。</summary>
    public string ProfileName { get; init; } = "FolderProfile";

    /// <summary>常用设置之外的其他属性（如 Platform、PublishProtocol、_TargetId）。</summary>
    public List<ProfileProperty> ExtraProperties { get; init; } = [];

    /// <summary>原始配置中需要保留的 ItemGroup 片段。</summary>
    public string? SourceItemGroupsXml { get; init; }

    /// <summary>压缩包设置；为 null 或 Enabled=false 时不打包。</summary>
    public ZipOptions? Zip { get; init; }

    /// <summary>MSI 安装包设置；为 null 或 Enabled=false 时不生成。</summary>
    public MsiOptions? Msi { get; init; }
}
