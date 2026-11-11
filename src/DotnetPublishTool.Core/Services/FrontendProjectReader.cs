using System.IO;
using System.Text.Json;
using DotnetPublishTool.Core.Models;

namespace DotnetPublishTool.Core.Services;

/// <summary>读取前端项目目录里的 package.json，不执行任何构建。</summary>
public sealed class FrontendProjectReader
{
    public const string PackageFileName = "package.json";

    /// <summary>读取项目信息；目录或 package.json 缺失、JSON 非法时抛 <see cref="PublishException"/>。</summary>
    public FrontendProjectInfo Read(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new PublishException("请选择前端项目目录。");
        }

        var fullPath = Path.GetFullPath(directory);
        if (!Directory.Exists(fullPath))
        {
            throw new PublishException("前端项目目录不存在：" + fullPath);
        }

        var packageJsonPath = Path.Combine(fullPath, PackageFileName);
        if (!File.Exists(packageJsonPath))
        {
            throw new PublishException("目录里没有 " + PackageFileName + "：" + fullPath);
        }

        string json;
        try
        {
            json = File.ReadAllText(packageJsonPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PublishException("无法读取 package.json：" + ex.Message, ex);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new PublishException("package.json 不是 JSON 对象：" + packageJsonPath);
            }

            var name = GetString(root, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                name = new DirectoryInfo(fullPath).Name;
            }

            var scripts = new List<string>();
            if (root.TryGetProperty("scripts", out var scriptsElement) && scriptsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in scriptsElement.EnumerateObject())
                {
                    scripts.Add(property.Name);
                }
            }

            return new FrontendProjectInfo
            {
                Directory = fullPath,
                PackageJsonPath = packageJsonPath,
                Name = name!,
                Version = GetString(root, "version"),
                Scripts = scripts,
            };
        }
        catch (JsonException ex)
        {
            throw new PublishException("package.json 不是合法的 JSON：" + ex.Message, ex);
        }
    }

    /// <summary>读取失败时返回 null，不抛异常。</summary>
    public FrontendProjectInfo? TryRead(string directory)
    {
        try
        {
            return Read(directory);
        }
        catch (Exception ex) when (ex is PublishException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
