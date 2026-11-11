namespace DotnetPublishTool.Core.Models;

/// <summary>一次「前端构建 + dist 打包」的请求。</summary>
public sealed class FrontendBuildRequest
{
    /// <summary>前端项目目录（含 package.json）。</summary>
    public required string ProjectDirectory { get; init; }

    /// <summary>用于压缩包命名的项目名，通常取目录名。</summary>
    public required string ProjectName { get; init; }

    public required FrontendPackageOptions Options { get; init; }

    /// <summary>输出根目录；为空时用默认目录。</summary>
    public string? OutputRoot { get; init; }

    /// <summary>是否先执行构建；false 表示直接打包现有 dist。</summary>
    public bool Build { get; init; } = true;
}
