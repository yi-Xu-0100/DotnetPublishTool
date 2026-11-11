using DotnetPublishTool.Core.Models;

namespace DotnetPublishTool.App.ViewModels;

/// <summary>包管理器下拉项（Label 显示、Value 绑定的枚举值）。</summary>
public sealed record FrontendPackageManagerOption(string Label, FrontendPackageManager Value);
