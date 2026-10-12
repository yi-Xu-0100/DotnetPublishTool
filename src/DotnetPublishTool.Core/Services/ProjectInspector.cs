using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using DotnetPublishTool.Core.Models;

namespace DotnetPublishTool.Core.Services;

/// <summary>读取项目文件并（尽量）通过 MSBuild 评估出准确属性。</summary>
public sealed class ProjectInspector
{
    private static readonly string[] ProjectExtensions = [".csproj", ".vbproj", ".fsproj"];

    private readonly DotnetProcessRunner _runner;

    public ProjectInspector(DotnetProcessRunner runner) => _runner = runner;

    public static bool IsProjectFile(string path) =>
        ProjectExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static bool IsSolutionFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ProjectInfo> InspectAsync(string projectPath, string? solutionPath = null, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(projectPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"找不到项目文件：{fullPath}", fullPath);
        }

        XDocument? document = null;
        try
        {
            document = XDocument.Load(fullPath);
        }
        catch
        {
            // 交给 MSBuild 评估阶段报错，这里仅退回静态解析。
        }

        var sdkName = (string?)document?.Root?.Attribute("Sdk")
            ?? (string?)document?.Root?.Elements("Sdk").FirstOrDefault()?.Attribute("Name");

        var targetFrameworks = new List<string>();
        var version = string.Empty;
        var outputType = string.Empty;
        var useWpf = false;
        var useWinForms = false;
        var isTestProject = false;
        string? evaluationWarning = null;

        var arguments = new List<string>
        {
            "msbuild", fullPath, "-nologo",
            "-getProperty:Version",
            "-getProperty:VersionPrefix",
            "-getProperty:VersionSuffix",
            "-getProperty:InformationalVersion",
            "-getProperty:AssemblyVersion",
            "-getProperty:FileVersion",
            "-getProperty:TargetFramework",
            "-getProperty:TargetFrameworks",
            "-getProperty:OutputType",
            "-getProperty:UseWPF",
            "-getProperty:UseWindowsForms",
            "-getProperty:IsTestProject",
        };

        try
        {
            var result = await _runner
                .RunAsync(arguments, Path.GetDirectoryName(fullPath), null, cancellationToken)
                .ConfigureAwait(false);

            if (result.Succeeded)
            {
                using var json = JsonDocument.Parse(ExtractJson(result.StandardOutput));
                var properties = json.RootElement.GetProperty("Properties");

                string Get(string name) =>
                    properties.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;

                var multi = Get("TargetFrameworks");
                if (!string.IsNullOrWhiteSpace(multi))
                {
                    targetFrameworks.AddRange(multi.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
                else if (!string.IsNullOrWhiteSpace(Get("TargetFramework")))
                {
                    targetFrameworks.Add(Get("TargetFramework"));
                }

                var versionProperty = Get("Version");
                if (string.IsNullOrWhiteSpace(versionProperty))
                {
                    var prefix = Get("VersionPrefix");
                    var suffix = Get("VersionSuffix");
                    versionProperty = string.IsNullOrWhiteSpace(prefix)
                        ? string.Empty
                        : (string.IsNullOrWhiteSpace(suffix) ? prefix : prefix + "-" + suffix);
                }

                // 只在「项目文件里显式声明」的版本键里挑：SDK 由 <Version> 推导出来的
                // AssemblyVersion/FileVersion/InformationalVersion 不算独立来源 —— 否则手动把 <Version>
                // 改小/改回去时会被推导值顶住，看起来就是「版本降不回来」。
                var candidates = new List<(string Name, string? Value)>();
                if (IsDeclared(document, "Version") || IsDeclared(document, "VersionPrefix"))
                {
                    candidates.Add(("Version", versionProperty));
                }

                foreach (var name in new[] { "InformationalVersion", "FileVersion", "AssemblyVersion" })
                {
                    if (IsDeclared(document, name))
                    {
                        candidates.Add((name, Get(name)));
                    }
                }

                if (candidates.Count == 0)
                {
                    // 版本写在 Directory.Build.props 之类的地方（项目文件里看不到）→ 退回 MSBuild 评估值。
                    candidates.Add(("Version", versionProperty));
                }

                version = PickBestVersion(candidates.ToArray()) ?? string.Empty;

                outputType = Get("OutputType");
                useWpf = IsTrue(Get("UseWPF"));
                useWinForms = IsTrue(Get("UseWindowsForms"));
                isTestProject = IsTrue(Get("IsTestProject"));
            }
            else
            {
                evaluationWarning = FirstError(result);
            }
        }
        catch (Exception ex)
        {
            evaluationWarning = "MSBuild 属性评估失败：" + ex.Message;
        }

        if (targetFrameworks.Count == 0 && document is not null)
        {
            var multi = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "TargetFrameworks")?.Value;
            if (!string.IsNullOrWhiteSpace(multi))
            {
                targetFrameworks.AddRange(multi.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
            else
            {
                var single = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "TargetFramework")?.Value;
                if (!string.IsNullOrWhiteSpace(single))
                {
                    targetFrameworks.Add(single.Trim());
                }
            }
        }

        if (string.IsNullOrWhiteSpace(outputType) && document is not null)
        {
            outputType = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "OutputType")?.Value ?? string.Empty;
        }

        var sdk = sdkName ?? string.Empty;
        var isSdkStyle = !string.IsNullOrWhiteSpace(sdk)
            || document?.Root?.Attribute("Sdk") is not null
            || document?.Descendants().Any(e => e.Name.LocalName == "Sdk") == true;

        return new ProjectInfo
        {
            Path = fullPath,
            Name = Path.GetFileNameWithoutExtension(fullPath),
            Directory = Path.GetDirectoryName(fullPath) ?? string.Empty,
            IsSdkStyle = isSdkStyle,
            Kind = DetectKind(sdk, outputType, useWpf, useWinForms, isTestProject),
            TargetFrameworks = targetFrameworks,
            Version = string.IsNullOrWhiteSpace(version) ? null : version,
            OutputType = string.IsNullOrWhiteSpace(outputType) ? null : outputType,
            SdkName = string.IsNullOrWhiteSpace(sdk) ? null : sdk,
            EvaluationWarning = evaluationWarning,
            SolutionPath = solutionPath,
        };
    }

    private static ProjectKind DetectKind(string sdk, string outputType, bool useWpf, bool useWinForms, bool isTestProject)
    {
        if (isTestProject) return ProjectKind.Test;
        if (sdk.Contains(".Web", StringComparison.OrdinalIgnoreCase)) return ProjectKind.Web;
        if (useWpf) return ProjectKind.Wpf;
        if (useWinForms) return ProjectKind.WinForms;
        if (sdk.Contains("WindowsDesktop", StringComparison.OrdinalIgnoreCase)) return ProjectKind.Wpf;
        if (outputType.Equals("Exe", StringComparison.OrdinalIgnoreCase)
            || outputType.Equals("WinExe", StringComparison.OrdinalIgnoreCase)) return ProjectKind.Console;
        if (outputType.Equals("Library", StringComparison.OrdinalIgnoreCase)) return ProjectKind.Library;
        return ProjectKind.Unknown;
    }

    private static bool IsTrue(string value) => bool.TryParse(value, out var parsed) && parsed;

    /// <summary>项目文件里是否显式声明了这个属性（不追 Directory.Build.props 等导入文件）。</summary>
    private static bool IsDeclared(XDocument? document, string name) =>
        document?.Descendants().Any(e => e.Name.LocalName == name) == true;

    /// <summary>
    /// 在候选版本键里取数值最大的那个（并列时按传入顺序，即优先 Version）。
    /// 候选只包含项目文件里显式声明的键：这样「只写了 AssemblyVersion/FileVersion/InformationalVersion、
    /// 没写 Version」的项目拿到的是应用真正在用的版本，而不是 SDK 默认的 1.0.0；
    /// 而手动把某个键改小时也会立刻生效。
    /// </summary>
    private static string? PickBestVersion(params (string Name, string? Value)[] candidates)
    {
        string? best = null;
        var bestFields = new List<int>();

        foreach (var (_, value) in candidates)
        {
            var fields = ParseVersionFields(value);
            if (fields.Count == 0)
            {
                continue;
            }

            if (best is null || CompareVersionFields(fields, bestFields) > 0)
            {
                best = value!.Trim();
                bestFields = fields;
            }
        }

        return best;
    }

    /// <summary>取版本号里的数字段（最多四段），丢掉 -prerelease / +metadata 与解析不出的尾巴。</summary>
    private static List<int> ParseVersionFields(string? version)
    {
        var fields = new List<int>(4);
        var text = (version ?? string.Empty).Trim();
        var cut = text.IndexOfAny(['-', '+']);
        if (cut >= 0)
        {
            text = text[..cut];
        }

        foreach (var part in text.Split('.'))
        {
            if (fields.Count == 4)
            {
                break;
            }

            if (!int.TryParse(part.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                break;
            }

            fields.Add(value);
        }

        return fields;
    }

    private static int CompareVersionFields(List<int> left, List<int> right)
    {
        for (var i = 0; i < 4; i++)
        {
            var a = i < left.Count ? left[i] : 0;
            var b = i < right.Count ? right[i] : 0;
            if (a != b)
            {
                return a < b ? -1 : 1;
            }
        }

        return 0;
    }

    private static string ExtractJson(string output)
    {
        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new FormatException("dotnet msbuild 未返回可解析的属性 JSON。");
        }

        return output[start..(end + 1)];
    }

    private static string FirstError(ProcessResult result)
    {
        var text = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        var line = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "未知错误";
        return "MSBuild 属性评估失败：" + line;
    }
}
