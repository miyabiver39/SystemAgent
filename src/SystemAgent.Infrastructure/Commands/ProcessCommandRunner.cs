using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SystemAgent.Infrastructure.Commands;

public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);

public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default);
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
        var useSudo = configuration.GetValue("CommandExecution:UseSudo", false);
        var startInfo = new ProcessStartInfo(useSudo ? "sudo" : executable)
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

        logger.LogDebug("コマンド実行: {Executable} {Arguments}", startInfo.FileName, string.Join(' ', startInfo.ArgumentList));

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new CommandResult(127, "", $"{executable} を実行できません: {ex.Message}");
        }
        process.StandardInput.Close();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var stdout = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            return new CommandResult(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException($"{executable} が {timeout.TotalSeconds:0} 秒以内に終了しませんでした。");
        }
    }
}
