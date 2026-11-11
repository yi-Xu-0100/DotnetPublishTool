using System.IO;
using System.Text.Json.Serialization;

namespace DotnetPublishTool.Core.Models;

/// <summary>最近使用的前端项目目录及其打包选项。</summary>
public sealed class RecentFrontendProject
{
    public string DirectoryPath { get; set; } = string.Empty;

    public DateTimeOffset LastUsed { get; set; } = DateTimeOffset.Now;

    /// <summary>该目录上次使用的构建与打包选项。</summary>
    public FrontendPackageOptions? Options { get; set; }

    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(DirectoryPath))
            {
                return string.Empty;
            }

            var trimmed = DirectoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(trimmed);
            var parent = Path.GetFileName(Path.GetDirectoryName(trimmed) ?? string.Empty);
            if (string.IsNullOrEmpty(name))
            {
                return DirectoryPath;
            }

            return string.IsNullOrEmpty(parent) ? name : name + " — " + parent;
        }
    }
}
