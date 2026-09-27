using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SystemAgent.Infrastructure.Commands;

namespace SystemAgent.Core.Tests;

/// <summary>実際に子プロセスを起動して、入出力の流し方と終了処理を確かめる（Windows は PowerShell、Linux は sh）。</summary>
public sealed class ProcessCommandRunnerTests
{
    private static readonly ProcessCommandRunner Runner =
        new(new ConfigurationBuilder().Build(), NullLogger<ProcessCommandRunner>.Instance);

    private static (string Executable, string[] Args) Shell(string powershell, string sh) =>
        OperatingSystem.IsWindows()
            ? ("powershell", ["-NoProfile", "-NonInteractive", "-Command", powershell])
            : ("sh", ["-c", sh]);

    [Fact]
    public async Task Streaming_LargeStandardError_DoesNotDeadlock_AndInputIgnoredByChildIsNotAnError()
    {
        // 標準入力を読まずに、パイプのバッファを超える量を標準エラーに出して終了する
        var (exe, args) = Shell("[Console]::Error.Write('x' * 300000); exit 3", "head -c 300000 /dev/zero | tr '\\0' x >&2; exit 3");
        using var input = new MemoryStream(new byte[8 * 1024 * 1024]);

        var result = await Runner.RunStreamingAsync(exe, args, input, null, TimeSpan.FromSeconds(60));

        Assert.Equal(3, result.ExitCode);
        Assert.Equal(300000, result.StandardError.Trim().Length);
    }

    [Fact]
    public async Task Streaming_PassesInputToOutput()
    {
        var (exe, args) = Shell("$input | ForEach-Object { $_ }", "cat");
        using var input = new MemoryStream("hello\nworld\n"u8.ToArray());
        using var output = new MemoryStream();

        var result = await Runner.RunStreamingAsync(exe, args, input, output, TimeSpan.FromSeconds(60));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["hello", "world"], System.Text.Encoding.UTF8.GetString(output.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    [Fact]
    public async Task Streaming_InputSourceFailure_TerminatesChildAndThrows()
    {
        var (exe, args) = Shell("$input | ForEach-Object { $_ } | Out-Null; Start-Sleep 30", "cat >/dev/null; sleep 30");
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<IOException>(() =>
            Runner.RunStreamingAsync(exe, args, new FailingStream(), null, TimeSpan.FromSeconds(60)));

        // 標準入力を閉じずに子プロセスを終了させる（閉じると途中までの入力で処理が進んでしまう）
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(25));
    }

    [Fact]
    public async Task Timeout_KillsChild()
    {
        var (exe, args) = Shell("Start-Sleep 30", "sleep 30");
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(() => Runner.RunAsync(exe, args, TimeSpan.FromSeconds(2)));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(25));
    }

    /// <summary>少し読めた後に失敗する読み込み元（バックアップファイルの読み込みエラー等）。</summary>
    private sealed class FailingStream : Stream
    {
        private int _reads;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_reads++ > 0) throw new IOException("読み込みに失敗しました。");
            buffer[offset] = (byte)'a';
            return 1;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_reads++ > 0) throw new IOException("読み込みに失敗しました。");
            buffer.Span[0] = (byte)'a';
            return ValueTask.FromResult(1);
        }
    }
}
