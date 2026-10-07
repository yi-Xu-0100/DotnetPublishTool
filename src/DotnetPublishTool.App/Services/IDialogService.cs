namespace DotnetPublishTool.App.Services;

/// <summary>界面交互抽象，便于自测时替换为静默实现。</summary>
public interface IDialogService
{
    /// <summary>选择项目或解决方案文件；取消返回 null。</summary>
    string? PickProjectFile();

    /// <summary>选择目录；取消返回 null。</summary>
    string? PickFolder(string? initialDirectory);

    void ShowError(string message, string? title = null);

    void ShowInfo(string message, string? title = null);
}
