namespace DotnetPublishTool.Core.Models;

/// <summary>一次发布（含可选打包）的结果。</summary>
public sealed class PublishResult
{
    public bool Success { get; init; }

    public int ExitCode { get; init; }

    /// <summary>实际发布目录。</summary>
    public string PublishDirectory { get; init; } = string.Empty;

    /// <summary>保存到默认目录的配置副本路径（未调用发布时为 null）。</summary>
    public string? ProfilePath { get; init; }

    /// <summary>生成的压缩包路径（未打包为 null）。</summary>
    public string? ZipPath { get; init; }

    public string CommandLine { get; init; } = string.Empty;

    public TimeSpan Duration { get; init; }

    public string? ErrorMessage { get; init; }

    /// <summary>已发布文件数。</summary>
    public int FileCount { get; init; }
}

/// <summary>只保存配置副本（不发布）时返回的信息。</summary>
public sealed class SavedProfile
{
    public SavedProfile(string profilePath, string publishDirectory)
    {
        ProfilePath = profilePath;
        PublishDirectory = publishDirectory;
    }

    public string ProfilePath { get; }

    public string PublishDirectory { get; }
}
