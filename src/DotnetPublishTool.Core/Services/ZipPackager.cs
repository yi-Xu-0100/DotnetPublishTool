using System.IO.Compression;

namespace DotnetPublishTool.Core.Services;

/// <summary>把发布目录打包成 zip。</summary>
public sealed class ZipPackager
{
    public void Create(string sourceDirectory, string zipPath, bool includeRootFolder, string rootFolderName)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException($"发布目录不存在：{sourceDirectory}");
        }

        var directory = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, file);
            var entryName = includeRootFolder ? Path.Combine(rootFolderName, relative) : relative;
            archive.CreateEntryFromFile(file, entryName.Replace('\\', '/'), CompressionLevel.Optimal);
        }
    }
}
