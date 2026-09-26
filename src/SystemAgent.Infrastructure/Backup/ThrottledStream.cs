namespace SystemAgent.Infrastructure.Backup;

/// <summary>
/// 読み書きの速度を上限（バイト/秒）以下に抑えるストリーム（ADR-010: バックアップ時の帯域制御をアプリ側で行う）。
/// ダンプの読み出しを絞ることで、パイプ越しにダンプコマンドとDBサーバー間の転送も絞られる。
/// </summary>
public sealed class ThrottledStream(Stream inner, long bytesPerSecond, TimeProvider? time = null) : Stream
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly long _start = (time ?? TimeProvider.System).GetTimestamp();
    private long _transferred;

    public long Transferred => _transferred;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // 1回の読み込みが大きすぎると制御が粗くなるため、上限の1/10秒分に分ける
        var chunk = bytesPerSecond > 0 ? buffer[..(int)Math.Min(buffer.Length, Math.Max(4096, bytesPerSecond / 10))] : buffer;
        var read = await inner.ReadAsync(chunk, cancellationToken);
        await AccountAsync(read, cancellationToken);
        return read;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken);
        await AccountAsync(buffer.Length, cancellationToken);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    private async Task AccountAsync(int bytes, CancellationToken cancellationToken)
    {
        _transferred += bytes;
        if (bytesPerSecond <= 0) return;

        // ここまでの転送量が上限どおりなら経過しているはずの時間まで待つ
        var expected = TimeSpan.FromSeconds((double)_transferred / bytesPerSecond);
        var elapsed = _time.GetElapsedTime(_start);
        if (expected > elapsed) await Task.Delay(expected - elapsed, _time, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        await base.DisposeAsync();
    }
}
