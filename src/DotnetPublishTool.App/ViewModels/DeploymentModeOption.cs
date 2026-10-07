namespace DotnetPublishTool.App.ViewModels;

/// <summary>「运行时依赖」下拉项：框架依赖（false） / 自包含（true）。</summary>
public sealed record DeploymentModeOption(string Label, bool Value);
