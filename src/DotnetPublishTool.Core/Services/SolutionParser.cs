using System.Xml.Linq;

namespace DotnetPublishTool.Core.Services;

/// <summary>从 .sln / .slnx 中解析出项目文件路径。</summary>
public static class SolutionParser
{
    public static IReadOnlyList<string> GetProjectPaths(string solutionPath)
    {
        var fullPath = Path.GetFullPath(solutionPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"找不到解决方案文件：{fullPath}", fullPath);
        }

        var directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
        var result = new List<string>();

        if (Path.GetExtension(fullPath).Equals(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            var document = XDocument.Load(fullPath);
            foreach (var element in document.Descendants().Where(e => e.Name.LocalName == "Project"))
            {
                var relative = (string?)element.Attribute("Path");
                if (!string.IsNullOrWhiteSpace(relative))
                {
                    AddIfProject(directory, relative!, result);
                }
            }
        }
        else
        {
            foreach (var line in File.ReadAllLines(fullPath))
            {
                var trimmed = line.TrimStart();
                if (!trimmed.StartsWith("Project(", StringComparison.Ordinal))
                {
                    continue;
                }

                // Project("{GUID}") = "Name", "relative\path.csproj", "{GUID}"
                var parts = trimmed.Split('"');
                if (parts.Length >= 6)
                {
                    AddIfProject(directory, parts[5], result);
                }
            }
        }

        return result;
    }

    private static void AddIfProject(string directory, string relativePath, List<string> result)
    {
        if (!ProjectInspector.IsProjectFile(relativePath))
        {
            return;
        }

        var candidate = Path.GetFullPath(Path.Combine(directory, relativePath.Replace('\\', Path.DirectorySeparatorChar)));
        if (File.Exists(candidate) && !result.Contains(candidate, StringComparer.OrdinalIgnoreCase))
        {
            result.Add(candidate);
        }
    }
}
