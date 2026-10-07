using DotnetPublishTool.Core.Models;

namespace DotnetPublishTool.Core.Services;

/// <summary>把发布请求翻译为 dotnet publish 命令行。</summary>
public sealed class PublishCommandBuilder
{
    public IReadOnlyList<string> Build(PublishRequest request, string profilePath, string publishDirectory)
    {
        var options = request.Options;
        var arguments = new List<string>
        {
            "publish",
            request.Project.Path,
            "-c",
            options.Configuration,
            "-p:PublishProfileFullPath=" + profilePath,
            "-o",
            publishDirectory,
        };

        if (!string.IsNullOrWhiteSpace(options.TargetFramework))
        {
            arguments.Add("-f");
            arguments.Add(options.TargetFramework!);
        }

        if (!string.IsNullOrWhiteSpace(options.RuntimeIdentifier))
        {
            arguments.Add("-r");
            arguments.Add(options.RuntimeIdentifier!);
        }

        // 四个开关总是显式传入，保证界面显示与产物一致。
        arguments.Add("-p:SelfContained=" + ToLower(options.SelfContained));
        arguments.Add("-p:PublishSingleFile=" + ToLower(options.PublishSingleFile));
        arguments.Add("-p:PublishReadyToRun=" + ToLower(options.PublishReadyToRun));
        arguments.Add("-p:PublishTrimmed=" + ToLower(options.PublishTrimmed));

        if (!string.IsNullOrWhiteSpace(options.EnvironmentName))
        {
            arguments.Add("-p:EnvironmentName=" + options.EnvironmentName);
        }

        if (!string.IsNullOrWhiteSpace(options.VersionOverride))
        {
            arguments.Add("-p:Version=" + options.VersionOverride);
        }

        if (options.NoRestore)
        {
            arguments.Add("--no-restore");
        }

        arguments.Add("-nologo");
        arguments.Add("-v");
        arguments.Add("minimal");
        return arguments;
    }

    public static string ToDisplayString(IReadOnlyList<string> arguments) =>
        "dotnet " + string.Join(' ', arguments.Select(Quote));

    private static string Quote(string argument) =>
        argument.Any(char.IsWhiteSpace) ? "\"" + argument + "\"" : argument;

    private static string ToLower(bool value) => value ? "true" : "false";
}
