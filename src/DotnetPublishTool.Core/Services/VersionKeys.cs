namespace DotnetPublishTool.Core.Services;

/// <summary>
/// MSBuild 的四个版本键。指定「版本覆盖」或自动递增时它们必须**一起**交给编译：
/// 项目只要显式写了 AssemblyVersion / FileVersion / InformationalVersion，单给 `-p:Version=` 是压不住它们的，
/// 结果就是安装包是新版本、应用自己报告的却还是旧版本（2026-10-13 反馈）。
/// </summary>
internal static class VersionKeys
{
    /// <summary>四个版本键，按「唯一来源 → 派生」的顺序。</summary>
    public static readonly string[] All = ["Version", "AssemblyVersion", "FileVersion", "InformationalVersion"];
}
