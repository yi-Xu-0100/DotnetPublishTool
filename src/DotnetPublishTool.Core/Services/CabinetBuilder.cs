using System.Diagnostics;
using System.Text;
using DotnetPublishTool.Core.Models;

namespace DotnetPublishTool.Core.Services;

/// <summary>
/// 用 Windows 自带的 makecab.exe 把暂存目录里的文件打成一个 cab。
/// 有意让 DDF 里只出现 ASCII 的条目名，并把 makecab 的工作目录设成暂存目录，
/// 这样中文/空格路径不会在 makecab 的 ANSI 解析里出错。
/// </summary>
internal static class CabinetBuilder
{
    public static void Create(string stagingDirectory, string cabinetName, IReadOnlyList<string> entryNames, IProgress<string>? log)
    {
        var directivePath = Path.Combine(stagingDirectory, "package.ddf");
        var builder = new StringBuilder();
        builder.AppendLine(".OPTION EXPLICIT");
        builder.AppendLine(".Set CabinetNameTemplate=" + cabinetName);
        builder.AppendLine(".Set DiskDirectory1=.");
        builder.AppendLine(".Set Cabinet=on");
        builder.AppendLine(".Set Compress=on");
        builder.AppendLine(".Set CompressionType=MSZIP");
        builder.AppendLine(".Set MaxDiskSize=0");
        builder.AppendLine(".Set MaxCabinetSize=0");
        builder.AppendLine(".Set CabinetFileCountThreshold=0");
        builder.AppendLine(".Set FolderFileCountThreshold=0");
        builder.AppendLine(".Set FolderSizeThreshold=0");
        builder.AppendLine(".Set UniqueFiles=off");
        foreach (var entry in entryNames)
        {
            builder.Append('"').Append(entry).Append("\" \"").Append(entry).AppendLine("\"");
        }

        File.WriteAllText(directivePath, builder.ToString(), Encoding.ASCII);

        var startInfo = new ProcessStartInfo
        {
            FileName = "makecab.exe",
            WorkingDirectory = stagingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("/F");
        startInfo.ArgumentList.Add("package.ddf");

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            throw new PublishException(
                "无法启动 makecab.exe（Windows 自带，位于 System32）。请确认系统完整。", ex);
        }

        if (process is null)
        {
            throw new PublishException("无法启动 makecab.exe（Windows 自带，位于 System32）。");
        }

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                log?.Report("[MSI] makecab 输出：" + detail.Trim());
                throw new PublishException($"makecab.exe 退出码 {process.ExitCode}，压缩 cab 失败。");
            }
        }

        if (!File.Exists(Path.Combine(stagingDirectory, cabinetName)))
        {
            throw new PublishException("makecab.exe 没有生成预期的 cab 文件：" + cabinetName);
        }
    }
}
