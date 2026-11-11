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

/// <summary>
/// 通用子进程执行器：直接启动可执行文件，或经 shell 执行命令行。
/// npm / pnpm / yarn 在 Windows 上是 .cmd 垫片，CreateProcess 不能直接启动，因此构建命令走 RunShellAsync。
/// </summary>
public sealed class ProcessRunner
{
    // stdout / stderr 分别在各自的线程上读取，而 Progress<T> 在同步上下文为空时会就地回调。
    // 这里把回调串行化，避免调用方（日志 StringBuilder、列表等）被并发写入。
    private readonly object _logLock = new();

    /// <summary>直接启动一个可执行文件（不经过 shell）。</summary>
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IProgress<string>? log,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        var startInfo = CreateStartInfo(fileName, workingDirectory);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        ApplyEnvironment(startInfo, environmentVariables);

        return await RunCoreAsync(startInfo, log, cancellationToken, fileName).ConfigureAwait(false);
    }

    /// <summary>经系统 shell 执行命令行；Windows 用 cmd.exe /d /s /c，其他平台用 /bin/sh -lc。</summary>
    public async Task<ProcessResult> RunShellAsync(
        string commandLine,
        string? workingDirectory,
        IProgress<string>? log,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        ProcessStartInfo startInfo;
        if (OperatingSystem.IsWindows())
        {
            startInfo = CreateStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), workingDirectory);
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(commandLine);
        }
        else
        {
            startInfo = CreateStartInfo("/bin/sh", workingDirectory);
            startInfo.ArgumentList.Add("-lc");
            startInfo.ArgumentList.Add(commandLine);
        }

        ApplyEnvironment(startInfo, environmentVariables);

        return await RunCoreAsync(startInfo, log, cancellationToken, commandLine).ConfigureAwait(false);
    }

    private static ProcessStartInfo CreateStartInfo(string fileName, string? workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory!;
        }

        return startInfo;
    }

    private static void ApplyEnvironment(ProcessStartInfo startInfo, IReadOnlyDictionary<string, string>? environmentVariables)
    {
        if (environmentVariables is null)
        {
            return;
        }

        foreach (var pair in environmentVariables)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }
    }

    private async Task<ProcessResult> RunCoreAsync(
        ProcessStartInfo startInfo,
        IProgress<string>? log,
        CancellationToken cancellationToken,
        string displayName)
    {
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

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("无法启动进程：" + displayName);
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                "无法启动进程：" + displayName + "。请确认命令已安装并加入 PATH。" + Environment.NewLine + ex.Message, ex);
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
