namespace DotnetPublishTool.Core.Models;

/// <summary>压缩包命名方式。</summary>
public enum ZipNamingMode
{
    /// <summary>项目名。</summary>
    ProjectName = 0,

    /// <summary>项目名_版本号。</summary>
    ProjectNameVersion = 1,

    /// <summary>项目名_日期。</summary>
    ProjectNameDate = 2,

    /// <summary>自定义名称。</summary>
    Custom = 3,
}

/// <summary>发布后打包 zip 的选项。</summary>
public sealed class ZipOptions
{
    /// <summary>日期命名的默认格式：精确到秒，避免同一天多次发布互相覆盖。</summary>
    public const string DefaultDateFormat = "yyyyMMdd_HHmmss";

    public bool Enabled { get; set; }

    public ZipNamingMode NamingMode { get; set; } = ZipNamingMode.ProjectNameDate;

    /// <summary>日期命名时使用的格式，默认 yyyyMMdd_HHmmss。</summary>
    public string DateFormat { get; set; } = DefaultDateFormat;

    /// <summary>压缩包内是否包含以项目名命名的顶层文件夹；默认不包含（解压即当前目录）。</summary>
    public bool IncludeRootFolder { get; set; }

    /// <summary>NamingMode=Custom 时使用。</summary>
    public string? CustomName { get; set; }

    /// <summary>用于「项目名_版本号」的版本号；由工作流在发布前补全。</summary>
    public string? Version { get; set; }

    /// <summary>压缩包输出目录；为空时使用默认项目目录。</summary>
    public string? OutputDirectory { get; set; }
}
