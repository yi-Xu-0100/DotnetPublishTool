using DotnetPublishTool.Core.Models;

namespace DotnetPublishTool.Core.Services;

/// <summary>按约定的三种口径解析压缩包文件名。</summary>
public static class ZipNaming
{
    public static string Resolve(ZipOptions options, string projectName)
    {
        var name = options.NamingMode switch
        {
            ZipNamingMode.ProjectName => projectName,
            ZipNamingMode.ProjectNameVersion => projectName + "_" + NormalizeVersion(options.Version),
            ZipNamingMode.ProjectNameDate => projectName + "_" + DateTime.Now.ToString(
                string.IsNullOrWhiteSpace(options.DateFormat) ? ZipOptions.DefaultDateFormat : options.DateFormat),
            ZipNamingMode.Custom => string.IsNullOrWhiteSpace(options.CustomName) ? projectName : options.CustomName!,
            _ => projectName,
        };

        return PathLayout.Sanitize(name) + ".zip";
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
