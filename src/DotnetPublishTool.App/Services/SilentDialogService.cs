namespace DotnetPublishTool.App.Services;

/// <summary>无界面场景（--selftest）下使用的对话框实现。</summary>
public sealed class SilentDialogService : IDialogService
{
    public List<string> Errors { get; } = [];

    public List<string> Infos { get; } = [];

    public string? PickProjectFile() => null;

    public string? PickFolder(string? initialDirectory) => null;

    public void ShowError(string message, string? title = null) => Errors.Add(message);

    public void ShowInfo(string message, string? title = null) => Infos.Add(message);
}
