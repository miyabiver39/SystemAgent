namespace SystemAgent.Infrastructure.Backup;

/// <summary>
/// 読み書きの速度を上限（バイト/秒）以下に抑えるストリーム（ADR-010: バックアップ時の帯域制御をアプリ側で行う）。
/// ダンプの読み出しを絞ることで、パイプ越しにダンプコマンドとDBサーバー間の転送も絞られる。
/// </summary>
/// <remarks>
/// トークンバケット方式。転送していない間に貯まる余裕は最大1秒分までとし、DB側の処理待ち等で転送が止まった後に
/// 上限を大きく超えて一気に流れる（過去の待ち時間を後から取り返す）ことがないようにする。
/// 同期の Read/Write（GZipStream 等が呼ぶ）は非同期版を待たずに、同期的に待機する。
/// </remarks>
public sealed class ThrottledStream(Stream inner, long bytesPerSecond, TimeProvider? time = null) : Stream
{
    /// <summary>貯められる余裕（バースト）の上限。上限速度の何秒分か。</summary>
    public static readonly TimeSpan MaxBurst = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly double _capacity = bytesPerSecond * MaxBurst.TotalSeconds;
    // 開始時点では余裕なし（最初から上限どおりに流す）
    private double _available;
    private long _last = (time ?? TimeProvider.System).GetTimestamp();
    private long _transferred;

    public long Transferred => _transferred;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(Chunk(buffer), cancellationToken);
        await WaitAsync(Account(read), cancellationToken);
        return read;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken);
        await WaitAsync(Account(buffer.Length), cancellationToken);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, Chunk(buffer.AsMemory(offset, count)).Length);
        Wait(Account(read));
        return read;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        Wait(Account(count));
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    // 1回の読み込みが大きすぎると制御が粗くなるため、上限の1/10秒分に分ける
    private Memory<byte> Chunk(Memory<byte> buffer) =>
        bytesPerSecond > 0 ? buffer[..(int)Math.Min(buffer.Length, Math.Max(4096, bytesPerSecond / 10))] : buffer;

    /// <summary>転送した分を差し引き、上限を超えていれば待つべき時間を返す。</summary>
    private TimeSpan Account(int bytes)
    {
        _transferred += bytes;
        if (bytesPerSecond <= 0) return TimeSpan.Zero;

        var now = _time.GetTimestamp();
        _available = Math.Min(_capacity, _available + _time.GetElapsedTime(_last, now).TotalSeconds * bytesPerSecond);
        _last = now;
        _available -= bytes;
        return _available >= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(-_available / bytesPerSecond);
    }

    private Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        delay > TimeSpan.Zero ? Task.Delay(delay, _time, cancellationToken) : Task.CompletedTask;

    private static void Wait(TimeSpan delay)
    {
        if (delay > TimeSpan.Zero) Thread.Sleep(delay);
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
