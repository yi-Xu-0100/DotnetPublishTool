using System.IO.Compression;
using System.Security.Cryptography;

namespace DotnetPublishTool.Core.Services;

/// <summary>把发布目录打包成 zip。</summary>
public sealed class ZipPackager
{
    public static Dictionary<string, string> Snapshot(string directory, CancellationToken cancellationToken)
    {
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(directory))
        {
            return snapshot;
        }
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = File.OpenRead(file);
            snapshot.Add(Path.GetRelativePath(directory, file), Convert.ToHexString(SHA256.HashData(stream)));
        }
        return snapshot;
    }

    public void Create(string sourceDirectory, string zipPath, bool includeRootFolder, string rootFolderName, IReadOnlySet<string>? includedFiles = null)
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
            if (includedFiles is not null && !includedFiles.Contains(relative))
            {
                continue;
            }
            var entryName = includeRootFolder ? Path.Combine(rootFolderName, relative) : relative;
            archive.CreateEntryFromFile(file, entryName.Replace('\\', '/'), CompressionLevel.Optimal);
        }
    }
}
