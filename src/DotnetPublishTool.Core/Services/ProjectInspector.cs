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

                version = Get("Version");
                if (string.IsNullOrWhiteSpace(version))
                {
                    var prefix = Get("VersionPrefix");
                    var suffix = Get("VersionSuffix");
                    version = string.IsNullOrWhiteSpace(prefix)
                        ? string.Empty
                        : (string.IsNullOrWhiteSpace(suffix) ? prefix : prefix + "-" + suffix);
                }

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
