namespace DotnetPublishTool.Core.Services;

/// <summary>以子进程方式调用 dotnet CLI，并把输出实时回传给界面。</summary>
public sealed class DotnetProcessRunner
{
    /// <summary>dotnet 可执行文件；默认走 PATH。</summary>
    public string DotnetExecutable { get; set; } = "dotnet";

    private readonly ProcessRunner _runner = new();

    public Task<ProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        var environment = new Dictionary<string, string>
        {
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_CLI_UI_LANGUAGE"] = "zh-Hans",
        };

        return _runner.RunAsync(DotnetExecutable, arguments, workingDirectory, log, cancellationToken, environment);
    }
}
