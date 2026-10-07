using System.Text;
using System.Xml;
using System.Xml.Linq;
using DotnetPublishTool.Core.Models;

namespace DotnetPublishTool.Core.Services;

/// <summary>发现 / 读取 / 写出发布配置（.pubxml）。</summary>
/// <remarks>
/// 只读源目录中的配置；用户修改后的配置一律写到输出目录的 profiles 子目录，
/// 从而做到「不修改源文件」。
/// </remarks>
public sealed class PublishProfileStore
{
    public const string DefaultProfileName = "FolderProfile";

    /// <summary>列出项目已有的发布配置；一个都没有时返回工具生成的默认配置。</summary>
    public IReadOnlyList<PublishProfile> Discover(ProjectInfo project)
    {
        var profiles = new List<PublishProfile>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in GetProfileDirectories(project))
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            // 按配置名（而非完整路径）排序：FolderProfile 排在 FolderProfile-SingleFile 之前。
            var candidates = Directory
                .EnumerateFiles(directory, "*.pubxml")
                .Where(f => !f.EndsWith(".pubxml.user", StringComparison.OrdinalIgnoreCase))
                .OrderBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase);

            foreach (var file in candidates)
            {
                var profile = TryLoad(file);
                if (profile is not null && seenNames.Add(profile.Name))
                {
                    profiles.Add(profile);
                }
            }
        }

        if (profiles.Count == 0)
        {
            profiles.Add(CreateDefault(project));
        }

        return profiles;
    }

    public static IEnumerable<string> GetProfileDirectories(ProjectInfo project)
    {
        yield return Path.Combine(project.Directory, "Properties", "PublishProfiles");
        yield return Path.Combine(project.Directory, "PublishProfiles");
    }

    public PublishProfile? TryLoad(string filePath)
    {
        try
        {
            return Load(filePath);
        }
        catch
        {
            return null;
        }
    }

    public PublishProfile Load(string filePath)
    {
        var document = XDocument.Load(filePath);
        var properties = new List<ProfileProperty>();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var itemGroups = new List<XElement>();

        if (document.Root is not null)
        {
            foreach (var element in document.Root.Elements())
            {
                if (element.Name.LocalName == "PropertyGroup")
                {
                    foreach (var property in element.Elements())
                    {
                        var key = property.Name.LocalName;
                        var value = property.Value;
                        if (index.TryGetValue(key, out var existing))
                        {
                            properties[existing] = new ProfileProperty(key, value);
                        }
                        else
                        {
                            index[key] = properties.Count;
                            properties.Add(new ProfileProperty(key, value));
                        }
                    }
                }
                else
                {
                    itemGroups.Add(new XElement(element));
                }
            }
        }

        return new PublishProfile
        {
            Name = Path.GetFileNameWithoutExtension(filePath),
            SourcePath = filePath,
            IsSynthetic = false,
            Properties = properties,
            ItemGroupsXml = itemGroups.Count == 0 ? null : string.Concat(itemGroups.Select(e => e.ToString())),
        };
    }

    /// <summary>为没有发布配置的项目生成一份可用的默认 Folder 配置。</summary>
    public PublishProfile CreateDefault(ProjectInfo project)
    {
        var framework = project.TargetFrameworks.FirstOrDefault() ?? "net10.0";
        var isExecutable = project.OutputType is "Exe" or "WinExe";

        var properties = new List<ProfileProperty>
        {
            new("Configuration", "Release"),
            new("Platform", "Any CPU"),
            new("PublishProtocol", "FileSystem"),
            new("_TargetId", "Folder"),
            new("TargetFramework", framework),
        };

        if (isExecutable)
        {
            properties.Add(new ProfileProperty("RuntimeIdentifier", "win-x64"));
        }

        properties.Add(new ProfileProperty("SelfContained", "false"));
        properties.Add(new ProfileProperty("PublishSingleFile", "false"));

        return new PublishProfile
        {
            Name = DefaultProfileName,
            SourcePath = null,
            IsSynthetic = true,
            Properties = properties,
        };
    }

    /// <summary>把配置写到指定目录，返回 .pubxml 的完整路径。</summary>
    public string Save(
        string directory,
        string profileName,
        IEnumerable<ProfileProperty> properties,
        string? itemGroupsXml)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, PathLayout.Sanitize(profileName) + ".pubxml");

        var propertyGroup = new XElement("PropertyGroup");
        foreach (var property in PublishOptionKeys.Order(properties))
        {
            if (string.IsNullOrWhiteSpace(property.Key))
            {
                continue;
            }

            propertyGroup.Add(new XElement(XName.Get(property.Key.Trim()), property.Value ?? string.Empty));
        }

        var root = new XElement("Project", propertyGroup);

        if (!string.IsNullOrWhiteSpace(itemGroupsXml))
        {
            try
            {
                // 原样保留源配置中的 ItemGroup（例如 MSDeploy 参数）。
                var wrapper = XElement.Parse("<Root>" + itemGroupsXml + "</Root>");
                foreach (var element in wrapper.Elements())
                {
                    root.Add(new XElement(element));
                }
            }
            catch
            {
                // 无法解析的片段直接丢弃，不影响属性部分。
            }
        }

        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        using (var writer = XmlWriter.Create(path, settings))
        {
            document.Save(writer);
        }

        return path;
    }
}
