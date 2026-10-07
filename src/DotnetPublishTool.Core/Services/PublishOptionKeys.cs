using DotnetPublishTool.Core.Models;

namespace DotnetPublishTool.Core.Services;

/// <summary>「常用设置」面板直接绑定的属性名；其余属性放在「其他属性」表格里原样保留。</summary>
public static class PublishOptionKeys
{
    public static readonly string[] Curated =
    [
        "Configuration",
        "TargetFramework",
        "RuntimeIdentifier",
        "SelfContained",
        "PublishSingleFile",
        "PublishReadyToRun",
        "PublishTrimmed",
        "EnvironmentName",
        "Version",
        "PublishDir",
    ];

    /// <summary>写回 .pubxml 时的推荐顺序（其余属性按出现的先后追加）。</summary>
    public static readonly string[] PreferredOrder =
    [
        "Configuration",
        "Platform",
        "PublishDir",
        "PublishProtocol",
        "_TargetId",
        "TargetFramework",
        "RuntimeIdentifier",
        "SelfContained",
        "PublishSingleFile",
        "PublishReadyToRun",
        "PublishTrimmed",
        "EnvironmentName",
        "Version",
        "DeleteExistingFiles",
    ];

    /// <summary>文件夹发布需要的默认标记；源配置已有同键时保留原值。</summary>
    public static readonly string[] FolderDefaults =
    [
        "PublishProtocol",
        "_TargetId",
    ];

    public static bool IsCurated(string key) => Curated.Contains(key, StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<ProfileProperty> Order(IEnumerable<ProfileProperty> properties)
    {
        var list = properties.ToList();
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in PreferredOrder)
        {
            var match = list.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
            if (match is not null && emitted.Add(match.Key))
            {
                yield return match;
            }
        }

        foreach (var property in list)
        {
            if (emitted.Add(property.Key))
            {
                yield return property;
            }
        }
    }
}
