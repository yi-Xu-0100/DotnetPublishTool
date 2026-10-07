namespace DotnetPublishTool.Core.Services;

/// <summary>输出目录的可用性检查与兜底候选。</summary>
/// <remarks>
/// 默认输出根目录在「文档」下。某些环境（受控文件夹访问、受限账户、沙箱/低完整性进程）
/// 会拒绝对该目录的写入，此时需要能检测出来并给出可写的替代目录，而不是抛原始异常。
/// </remarks>
public static class OutputDirectoryService
{
    /// <summary>按优先级列出可写候选目录（每个都以工具自己的子目录结尾）。</summary>
    public static IReadOnlyList<string> GetFallbackRoots()
    {
        var roots = new List<string>();

        Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Add(Path.GetTempPath());

        return roots;

        void Add(string? basePath)
        {
            if (string.IsNullOrWhiteSpace(basePath))
            {
                return;
            }

            try
            {
                roots.Add(Path.Combine(Path.GetFullPath(basePath), PathLayout.DefaultOutputFolderName));
            }
            catch
            {
                // 忽略无法规整的路径。
            }
        }
    }

    /// <summary>确保目录存在且确实可写：成功返回 null，失败返回中文原因。</summary>
    public static string? TryEnsureWritable(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return "目录为空";
        }

        try
        {
            var fullPath = Path.GetFullPath(directory);
            Directory.CreateDirectory(fullPath);

            // 用一次真实的写操作探测（DeleteOnClose 保证不留残留）。
            var probe = Path.Combine(fullPath, ".dpt-write-probe-" + Guid.NewGuid().ToString("N") + ".tmp");
            using var stream = new FileStream(
                probe,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
            stream.WriteByte(0);
            return null;
        }
        catch (Exception ex)
        {
            return Describe(ex);
        }
    }

    /// <summary>把常见的目录异常翻译成可读原因。</summary>
    public static string Describe(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "访问被拒绝：当前账户或运行环境（受限账户 / 受控文件夹访问 / 沙箱）对该目录没有写权限",
        DirectoryNotFoundException => "路径不存在或对应驱动器不可用",
        PathTooLongException => "路径过长",
        IOException io => "IO 错误：" + io.Message,
        _ => exception.Message,
    };
}
