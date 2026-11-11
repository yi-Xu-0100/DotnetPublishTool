namespace DotnetPublishTool.Core.Models;

/// <summary>从 package.json 读到的前端项目基本信息。</summary>
public sealed class FrontendProjectInfo
{
    /// <summary>项目目录（绝对路径）。</summary>
    public required string Directory { get; init; }

    /// <summary>package.json 的绝对路径。</summary>
    public required string PackageJsonPath { get; init; }

    /// <summary>package.json 里的 name；缺失时回退为目录名。</summary>
    public required string Name { get; init; }

    public string? Version { get; init; }

    /// <summary>package.json 里 scripts 的键名，按文件顺序。</summary>
    public IReadOnlyList<string> Scripts { get; init; } = [];

    public string DisplayName => string.IsNullOrWhiteSpace(Version) ? Name : Name + " " + Version;

    /// <summary>界面上的单行摘要。</summary>
    public string Summary
    {
        get
        {
            var scripts = Scripts.Count == 0
                ? "（无）"
                : string.Join("、", Scripts.Take(8)) + (Scripts.Count > 8 ? " 等" : string.Empty);
            var hasBuild = Scripts.Any(s => string.Equals(s, "build", StringComparison.OrdinalIgnoreCase));
            var buildHint = hasBuild ? string.Empty : "（未发现 build 脚本）";
            return $"package.json：{DisplayName}；脚本：{scripts}{buildHint}";
        }
    }
}
