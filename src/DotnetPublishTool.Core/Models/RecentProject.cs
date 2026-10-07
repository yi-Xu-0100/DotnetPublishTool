using System.IO;
using System.Text.Json.Serialization;

namespace DotnetPublishTool.Core.Models;

/// <summary>最近选择的项目 / 解决方案记录，用于下次快速复选。</summary>
public sealed class RecentProject
{
    /// <summary>项目或解决方案文件的绝对路径。</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>最后一次选择（或载入）的时间。</summary>
    public DateTimeOffset LastUsed { get; set; }

    /// <summary>
    /// 下拉列表里显示的名称：文件名 —— 末尾两级目录。
    /// 同名项目分布在不同仓库时，靠这两级目录区分；完整路径见下拉的悬停提示。
    /// </summary>
    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            var fileName = Path.GetFileName(FilePath);
            if (string.IsNullOrEmpty(fileName))
            {
                return FilePath;
            }

            var directory = Path.GetDirectoryName(FilePath);
            if (string.IsNullOrEmpty(directory))
            {
                return fileName;
            }

            var parent = Path.GetFileName(directory);
            var grandParent = Path.GetFileName(Path.GetDirectoryName(directory) ?? string.Empty);
            var suffix = string.IsNullOrEmpty(grandParent)
                ? parent
                : grandParent + Path.DirectorySeparatorChar + parent;

            return string.IsNullOrEmpty(suffix) ? fileName : fileName + "  —  " + suffix;
        }
    }
}
