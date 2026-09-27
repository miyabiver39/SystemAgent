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
            // 標準入力・標準出力・標準エラーは同時に流す。どれかを後回しにすると、子プロセスがパイプのバッファ（約64KB）を
            // 使い切って書き込みで止まり、こちらは別のストリームの完了を待ったまま互いに待ち続ける
            var stderr = process.StandardError.ReadToEndAsync(token);
            var output = process.StandardOutput.BaseStream.CopyToAsync(stdout ?? Stream.Null, token);
            try
            {
                await WriteInputAsync(process, stdin, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 入力の読み込み元が失敗した。標準入力を閉じると途中までの入力で処理（復元等）が進んでしまうため、先に終了させる
                Terminate(process, executable);
                throw;
            }
            await Task.WhenAll(output, stderr);
            await process.WaitForExitAsync(token);
            return new CommandResult(process.ExitCode, "", await stderr);
        });
    }

    /// <summary>
    /// 標準入力に流して閉じる。子プロセスが入力を読み切らずに終了した場合（復元の失敗等）は書き込みが IOException になるが、
    /// 失敗の理由は終了コードと標準エラーで返すため、書き込み側の IOException は無視する。
    /// 読み込み元（バックアップファイル等）の失敗は途中までの入力で成功扱いにならないよう、そのまま例外にする。
    /// </summary>
    private static async Task WriteInputAsync(Process process, Stream? stdin, CancellationToken cancellationToken)
    {
        // 読み込み元の失敗・取り消しでは標準入力を閉じない（呼び出し側で子プロセスを終了させる）
        if (stdin is not null)
        {
            var target = process.StandardInput.BaseStream;
            var buffer = new byte[81920];
            int read;
            while ((read = await stdin.ReadAsync(buffer, cancellationToken)) > 0)
            {
                try
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                catch (IOException)
                {
                    // 子プロセス側でパイプが閉じられた
                    break;
                }
            }
        }
        CloseInput(process);
    }

    private static void CloseInput(Process process)
    {
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // 子プロセス側でパイプが閉じられた
        }
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

        // 引数にはデプロイの環境変数など秘密情報が含まれ得るため、伏せ字にしてから出す
        logger.LogDebug("コマンド実行: {Command}", CommandArguments.Describe(startInfo.FileName, startInfo.ArgumentList));

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

    private async Task<CommandResult> WaitAsync(Process process, string executable, TimeSpan timeout,
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
            Terminate(process, executable);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException($"{executable} が {timeout.TotalSeconds:0} 秒以内に終了しませんでした。");
        }
    }

    /// <summary>
    /// タイムアウト・取り消し時に子プロセス（sudo 経由ならその先も含む）を終了させ、終了を待って回収する。
    /// 回収しないと終了した子プロセスが残り続ける（ゾンビ）。
    /// </summary>
    private void Terminate(Process process, string executable)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // すでに終了している
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            logger.LogWarning("{Executable} を終了できませんでした: {Message}", executable, ex.Message);
        }

        try
        {
            if (!process.WaitForExit(TerminateWait))
                logger.LogWarning("{Executable} が終了要求から {Seconds} 秒以内に終了しませんでした。", executable, TerminateWait.TotalSeconds);
        }
        catch (InvalidOperationException)
        {
            // 起動していない・すでに回収済み
        }
    }

    private static readonly TimeSpan TerminateWait = TimeSpan.FromSeconds(5);

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
