namespace DotnetPublishTool.Core.Models;

/// <summary>前端构建与打包的结果。</summary>
public sealed class FrontendBuildResult
{
    public bool Success { get; init; }

    public int ExitCode { get; init; }

    /// <summary>实际执行的命令行；构建被跳过时仍给出将要执行的命令，便于核对。</summary>
    public string CommandLine { get; init; } = string.Empty;

    public string? DistDirectory { get; init; }

    public string? ZipPath { get; init; }

    /// <summary>dist 里的文件数。</summary>
    public int FileCount { get; init; }

    public TimeSpan Duration { get; init; }

    public string? ErrorMessage { get; init; }
}
