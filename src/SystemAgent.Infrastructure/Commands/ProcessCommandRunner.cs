using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SystemAgent.Infrastructure.Commands;

public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);

public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// 大きな入出力（DBダンプ等）をメモリに載せずに流す。stdin/stdoutはnull可。戻り値のStandardOutputは常に空。
    /// </summary>
    Task<CommandResult> RunStreamingAsync(string executable, IReadOnlyList<string> arguments, Stream? stdin, Stream? stdout,
        TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>
/// OSコマンドを実行する。シェルは介さず引数を個別に渡すため、引数値によるコマンドインジェクションは起きない。
/// CommandExecution:UseSudo=true の場合は `sudo -n` 経由で実行する（QA 0004）。
/// </summary>
public sealed class ProcessCommandRunner(IConfiguration configuration, ILogger<ProcessCommandRunner> logger) : ICommandRunner
{
    public async Task<CommandResult> RunAsync(
        string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var process = Start(executable, arguments);
        if (process is null) return new CommandResult(127, "", $"{executable} を実行できません。");
        process.StandardInput.Close();

        return await WaitAsync(process, executable, timeout, cancellationToken, async token =>
        {
            var stdout = process.StandardOutput.ReadToEndAsync(token);
            var stderr = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            return new CommandResult(process.ExitCode, await stdout, await stderr);
        });
    }

    public async Task<CommandResult> RunStreamingAsync(string executable, IReadOnlyList<string> arguments, Stream? stdin, Stream? stdout,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var process = Start(executable, arguments);
        if (process is null) return new CommandResult(127, "", $"{executable} を実行できません。");

        return await WaitAsync(process, executable, timeout, cancellationToken, async token =>
        {
            var stderr = process.StandardError.ReadToEndAsync(token);
            var input = Task.Run(async () =>
            {
                if (stdin is not null) await stdin.CopyToAsync(process.StandardInput.BaseStream, token);
                process.StandardInput.Close();
            }, token);
            var output = stdout is null
                ? process.StandardOutput.ReadToEndAsync(token)
                : process.StandardOutput.BaseStream.CopyToAsync(stdout, token).ContinueWith(_ => "", token);
            await Task.WhenAll(input, output);
            await process.WaitForExitAsync(token);
            return new CommandResult(process.ExitCode, "", await stderr);
        });
    }

    private Process? Start(string executable, IReadOnlyList<string> arguments)
    {
        var useSudo = configuration.GetValue("CommandExecution:UseSudo", false);
        var startInfo = new ProcessStartInfo(Resolve(useSudo ? "sudo" : executable))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        if (useSudo)
        {
            startInfo.ArgumentList.Add("-n");
            startInfo.ArgumentList.Add(executable);
        }
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        if (!OperatingSystem.IsWindows()) startInfo.Environment["PATH"] = WithSystemPaths(startInfo.Environment["PATH"]);

        logger.LogDebug("コマンド実行: {Executable} {Arguments}", startInfo.FileName, string.Join(' ', startInfo.ArgumentList));

        var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
            return process;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            logger.LogWarning("{Executable} を実行できません: {Message}", executable, ex.Message);
            process.Dispose();
            return null;
        }
    }

    private static async Task<CommandResult> WaitAsync(Process process, string executable, TimeSpan timeout,
        CancellationToken cancellationToken, Func<CancellationToken, Task<CommandResult>> body)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await body(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException($"{executable} が {timeout.TotalSeconds:0} 秒以内に終了しませんでした。");
        }
    }

    // ip, keepalived, chronyd 等の管理コマンドは sbin にある。起動方法によってはPATHに含まれないため補う
    private static readonly string[] SystemPaths = ["/usr/local/sbin", "/usr/local/bin", "/usr/sbin", "/usr/bin", "/sbin", "/bin"];

    /// <summary>.NETは親プロセスのPATHで実行ファイルを探すため、sbinを補ったPATHで自前で解決する。</summary>
    private static string Resolve(string executable)
    {
        if (OperatingSystem.IsWindows() || executable.Contains('/')) return executable;
        return WithSystemPaths(Environment.GetEnvironmentVariable("PATH")).Split(':')
            .Select(dir => Path.Combine(dir, executable))
            .FirstOrDefault(File.Exists) ?? executable;
    }

    public static string WithSystemPaths(string? path)
    {
        var entries = (path ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).ToList();
        entries.AddRange(SystemPaths.Where(p => !entries.Contains(p)));
        return string.Join(':', entries);
    }
}
