using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SystemAgent.Client;

/// <summary>アクセストークンの保管先。WebUIはブラウザのsessionStorage、CLIはユーザーのホームディレクトリ。</summary>
public interface ITokenProvider
{
    ValueTask<string?> GetAccessTokenAsync();

    /// <summary>APIが401を返した（トークン失効）ときに呼ばれる。</summary>
    ValueTask OnUnauthorizedAsync();
}

/// <summary>
/// WebAPIのクライアント。WebUIとCLIはこのクラスだけを通してAPIを使い、業務ロジックを持たない（基本設計書 9章、ADR-007）。
/// 両者の操作が同一であることはこの共有によって担保する。
/// </summary>
public sealed partial class ApiClient(HttpClient http, ITokenProvider tokens)
{
    // ForNodeで作ったクライアントは、api/... を api/nodes/{id}/proxy/api/... に読み替えて他ノードに転送させる
    private string? _nodePrefix;

    /// <summary>指定ノード（nullならこのノード）を操作するクライアント。ログイン等の認証APIはこのノードで行う。</summary>
    public ApiClient ForNode(Guid? nodeId) =>
        nodeId is null ? this : new ApiClient(http, tokens) { _nodePrefix = $"api/nodes/{nodeId}/proxy/" };

    /// <summary>通常の操作（一覧の取得・設定の保存など）の応答を待つ時間。サーバーが応答しないときに長く待たせない。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>サーバー側でOSコマンドの完了を待つ操作（サービス・コンテナの起動停止等。テンプレート上の上限180秒）の応答を待つ時間。</summary>
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(4);

    /// <summary>イメージの pull/push/取り込み・デプロイ・DBのバックアップと復元（テンプレート上の上限30分）の応答を待つ時間。</summary>
    public static readonly TimeSpan LongOperationTimeout = TimeSpan.FromMinutes(35);

    /// <summary>
    /// HttpClient 自体のタイムアウト。要求ごとの待ち時間（DefaultTimeout / CommandTimeout / LongOperationTimeout）で打ち切るため、
    /// 最も長いものに合わせる。
    /// </summary>
    public static readonly TimeSpan HttpTimeout = LongOperationTimeout;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <param name="timeout">応答を待つ時間（省略時は DefaultTimeout）。</param>
    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, bool authorize, CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        using var response = await SendCoreAsync(method, path, body, authorize, cancellationToken, timeout: timeout);
        return (await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken))!;
    }

    private async Task SendAndDisposeAsync(HttpMethod method, string path, object? body, bool authorize, CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        using var _ = await SendCoreAsync(method, path, body, authorize, cancellationToken, timeout: timeout);
    }

    /// <param name="completion">ResponseHeadersRead の場合、timeout は応答ヘッダを受け取るまでに適用する（本文の読み込みには適用しない）。</param>
    private async Task<HttpResponseMessage> SendCoreAsync(
        HttpMethod method, string path, object? body, bool authorize, CancellationToken cancellationToken,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead, TimeSpan? timeout = null)
    {
        if (_nodePrefix is not null && path.StartsWith("api/", StringComparison.Ordinal))
        {
            path = _nodePrefix + path;
            // 他ノードへの転送APIは認証が必要（転送先で認証不要なAPIでも）
            authorize = true;
        }
        using var request = new HttpRequestMessage(method, path);
        request.Content = body switch
        {
            null => null,
            HttpContent content => content,
            _ => JsonContent.Create(body, options: Json),
        };
        if (authorize && await tokens.GetAccessTokenAsync() is { } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var limit = timeout ?? DefaultTimeout;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(limit);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, completion, timeoutCts.Token);
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"サーバーから {limit.TotalSeconds:0} 秒以内に応答がありませんでした。処理がサーバー側で続いている場合があるため、状態を確認してください。", ex);
        }
        if (response.IsSuccessStatusCode) return response;

        using (response)
        {
            if (authorize && response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await tokens.OnUnauthorizedAsync();
            }
            throw new ApiException(response.StatusCode, await ReadProblemAsync(response, cancellationToken));
        }
    }

    private static async Task<string?> ReadProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<Problem>(Json, cancellationToken);
            if (problem?.Errors is { Count: > 0 } errors) return string.Join(" ", errors.SelectMany(e => e.Value));
            // NotFound() 等は英語のTitleだけなので、よく返す状態コードは日本語にする
            return problem?.Detail ?? response.StatusCode switch
            {
                HttpStatusCode.NotFound => "対象が見つかりません（削除済みか、名前が違います）。",
                HttpStatusCode.Forbidden => "この操作は許可されていません。",
                _ => problem?.Title,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record Problem(string? Title, string? Detail, Dictionary<string, string[]>? Errors);
}

public sealed class ApiException(HttpStatusCode statusCode, string? detail)
    : Exception(detail ?? $"APIエラー ({(int)statusCode})")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

/// <summary>応答本文のストリーム。破棄時に応答も破棄する。</summary>
internal sealed class ResponseStream(HttpResponseMessage response, Stream inner) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
            response.Dispose();
        }
        base.Dispose(disposing);
    }
}
