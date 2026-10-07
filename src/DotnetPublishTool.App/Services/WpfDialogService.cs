using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace DotnetPublishTool.App.Services;

public sealed class WpfDialogService : IDialogService
{
    public string? PickProjectFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 dotnet 项目或解决方案",
            Filter = "dotnet 项目 (*.csproj;*.vbproj;*.fsproj)|*.csproj;*.vbproj;*.fsproj|"
                + "解决方案 (*.sln;*.slnx)|*.sln;*.slnx|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickFolder(string? initialDirectory)
    {
        var dialog = new OpenFolderDialog { Title = "选择目录", Multiselect = false };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public void ShowError(string message, string? title = null) =>
        MessageBox.Show(message, title ?? "出错了", MessageBoxButton.OK, MessageBoxImage.Error);

    public void ShowInfo(string message, string? title = null) =>
        MessageBox.Show(message, title ?? "提示", MessageBoxButton.OK, MessageBoxImage.Information);
}
