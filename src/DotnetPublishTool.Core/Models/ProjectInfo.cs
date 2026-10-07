namespace DotnetPublishTool.Core.Models;

/// <summary>项目类型（用于推断默认发布配置与提示）。</summary>
public enum ProjectKind
{
    Unknown,
    Console,
    Library,
    Wpf,
    WinForms,
    Web,
    Test,
}

/// <summary>一个 dotnet 项目文件及其评估后的基本信息。</summary>
public sealed class ProjectInfo
{
    /// <summary>项目文件的绝对路径。</summary>
    public required string Path { get; init; }

    /// <summary>项目名（不含扩展名）。</summary>
    public required string Name { get; init; }

    /// <summary>项目所在目录。</summary>
    public required string Directory { get; init; }

    /// <summary>是否为 SDK 风格项目；非 SDK 风格项目无法用 dotnet publish 发布。</summary>
    public bool IsSdkStyle { get; init; }

    /// <summary>项目类型。</summary>
    public ProjectKind Kind { get; init; }

    /// <summary>目标框架列表（多目标时为多项）。</summary>
    public IReadOnlyList<string> TargetFrameworks { get; init; } = [];

    /// <summary>项目版本号（来自 MSBuild 的 Version，评估失败时可能为空）。</summary>
    public string? Version { get; init; }

    /// <summary>OutputType（Exe / WinExe / Library）。</summary>
    public string? OutputType { get; init; }

    /// <summary>使用的 SDK 名，例如 Microsoft.NET.Sdk。</summary>
    public string? SdkName { get; init; }

    /// <summary>属性评估失败时的原因（用于界面提示）。</summary>
    public string? EvaluationWarning { get; init; }

    /// <summary>来源解决方案路径（若由解决方案解析而来）。</summary>
    public string? SolutionPath { get; init; }

    public string KindText => Kind switch
    {
        ProjectKind.Console => "控制台",
        ProjectKind.Library => "类库",
        ProjectKind.Wpf => "WPF 桌面",
        ProjectKind.WinForms => "WinForms 桌面",
        ProjectKind.Web => "Web",
        ProjectKind.Test => "测试",
        _ => "未知",
    };

    public string FrameworkText => TargetFrameworks.Count == 0 ? "-" : string.Join(" / ", TargetFrameworks);

    public string Summary => $"{KindText} · {FrameworkText} · 版本 {Version ?? "未解析"}";
}
