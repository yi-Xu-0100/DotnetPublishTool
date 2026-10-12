using DotnetPublishTool.Core.Models;

namespace DotnetPublishTool.Core.Services;

/// <summary>按约定的三种口径解析压缩包文件名。</summary>
public static class ZipNaming
{
    public static string Resolve(ZipOptions options, string projectName) =>
        Resolve(options.NamingMode, options.DateFormat, options.CustomName, options.Version, projectName, ".zip");

    /// <summary>
    /// 按命名口径解析包名。MSI 安装包沿用同一套口径，只把扩展名换成 .msi，
    /// 让「zip / 安装包」两种产物在界面上是同一套命名习惯。
    /// </summary>
    public static string Resolve(
        ZipNamingMode mode,
        string? dateFormat,
        string? customName,
        string? version,
        string baseName,
        string extension)
    {
        var name = mode switch
        {
            ZipNamingMode.ProjectName => baseName,
            ZipNamingMode.ProjectNameVersion => baseName + "_" + NormalizeVersion(version),
            ZipNamingMode.ProjectNameDate => baseName + "_" + DateTime.Now.ToString(
                string.IsNullOrWhiteSpace(dateFormat) ? ZipOptions.DefaultDateFormat : dateFormat),
            ZipNamingMode.Custom => string.IsNullOrWhiteSpace(customName) ? baseName : customName!,
            _ => baseName,
        };

        return PathLayout.Sanitize(name) + extension;
    }

    public static string Describe(ZipNamingMode mode) => mode switch
    {
        ZipNamingMode.ProjectName => "项目名",
        ZipNamingMode.ProjectNameVersion => "项目名_版本号",
        ZipNamingMode.ProjectNameDate => "项目名_日期",
        ZipNamingMode.Custom => "自定义",
        _ => mode.ToString(),
    };

    /// <summary>去掉语义化版本里的 +buildmetadata，并规避非法文件名字符。</summary>
    private static string NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return "unknown";
        }

        var value = version.Trim();
        var plus = value.IndexOf('+');
        if (plus >= 0)
        {
            value = value[..plus];
        }

        return string.IsNullOrWhiteSpace(value) ? "unknown" : value;
    }
}
