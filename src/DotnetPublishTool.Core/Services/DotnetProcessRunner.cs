using System.Diagnostics;
using System.Text;

namespace DotnetPublishTool.Core.Services;

public sealed class ProcessResult
{
    public int ExitCode { get; init; }

    public string StandardOutput { get; init; } = string.Empty;

    public string StandardError { get; init; } = string.Empty;

    public bool Succeeded => ExitCode == 0;
}

/// <summary>以子进程方式调用 dotnet CLI，并把输出实时回传给界面。</summary>
public sealed class DotnetProcessRunner
{
    /// <summary>dotnet 可执行文件；默认走 PATH。</summary>
    public string DotnetExecutable { get; set; } = "dotnet";

    // stdout / stderr 分别在各自的线程上读取，而 Progress<T> 在同步上下文为空时会就地回调。
    // 这里把回调串行化，避免调用方（日志 StringBuilder、列表等）被并发写入。
    private readonly object _logLock = new();

    public async Task<ProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = DotnetExecutable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory!;
        }

        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "zh-Hans";

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        using var outputDone = new ManualResetEventSlim(false);
        using var errorDone = new ManualResetEventSlim(false);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                outputDone.Set();
                return;
            }

            standardOutput.AppendLine(e.Data);
            ReportLine(log, e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                errorDone.Set();
                return;
            }

            standardError.AppendLine(e.Data);
            ReportLine(log, e.Data);
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("无法启动 dotnet 进程，请确认已安装 .NET SDK 并加入 PATH。");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // 进程可能刚好退出，忽略。
            }
        });

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        outputDone.Wait(TimeSpan.FromSeconds(10));
        errorDone.Wait(TimeSpan.FromSeconds(10));

        return new ProcessResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = standardOutput.ToString(),
            StandardError = standardError.ToString(),
        };
    }

    private void ReportLine(IProgress<string>? log, string line)
    {
        if (log is null)
        {
            return;
        }

        lock (_logLock)
        {
            log.Report(line);
        }
    }
}
