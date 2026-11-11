namespace DotnetPublishTool.Core.Models;

/// <summary>前端项目的包管理器。</summary>
public enum FrontendPackageManager
{
    /// <summary>pnpm（默认）。</summary>
    Pnpm = 0,

    /// <summary>npm。</summary>
    Npm = 1,

    /// <summary>yarn。</summary>
    Yarn = 2,
}

/// <summary>前端构建与 dist 打包选项；随前端项目一起保存。</summary>
public sealed class FrontendPackageOptions
{
    /// <summary>默认构建参数：run build。</summary>
    public const string DefaultBuildArguments = "run build";

    /// <summary>默认构建产物目录。</summary>
    public const string DefaultDistDirectory = "dist";

    public FrontendPackageManager PackageManager { get; set; } = FrontendPackageManager.Pnpm;

    /// <summary>传给包管理器的构建参数，默认 "run build"（即 pnpm run build）。</summary>
    public string BuildArguments { get; set; } = DefaultBuildArguments;

    /// <summary>构建产物目录；相对路径按前端项目目录解析，默认 dist。</summary>
    public string DistDirectory { get; set; } = DefaultDistDirectory;

    /// <summary>压缩包输出目录；为空时使用「输出根目录\项目目录名」。</summary>
    public string? OutputDirectory { get; set; }

    public ZipNamingMode NamingMode { get; set; } = ZipNamingMode.ProjectNameDate;

    /// <summary>日期命名时使用的格式，默认 yyyyMMdd_HHmmss。</summary>
    public string DateFormat { get; set; } = ZipOptions.DefaultDateFormat;

    /// <summary>压缩包内是否包含以项目名命名的顶层文件夹。</summary>
    public bool IncludeRootFolder { get; set; }

    /// <summary>NamingMode=Custom 时使用。</summary>
    public string? CustomName { get; set; }
}
