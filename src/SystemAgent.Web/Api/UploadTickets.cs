using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace SystemAgent.Web.Api;

/// <summary>
/// ブラウザから大きなファイルを直接アップロードさせるための使い捨てチケット。
/// WebUI（Blazor）はトークンをブラウザのJavaScriptに渡さないため、ログイン中のユーザー・操作対象ノードに紐づけたチケットを
/// サーバー側で発行し、アップロード先URLに含める。1回限り・発行から10分以内に使用。
/// </summary>
public sealed class UploadTickets(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, UploadTicket> _tickets = new();

    /// <param name="Actor">監査ログに残す操作者。</param>
    /// <param name="NodeId">取り込み先ノード。nullはこのノード。</param>
    public sealed record UploadTicket(string Actor, Guid? NodeId, DateTimeOffset ExpiresAt);

    public string Create(string actor, Guid? nodeId)
    {
        var now = time.GetUtcNow();
        foreach (var (key, expired) in _tickets.Where(t => t.Value.ExpiresAt <= now)) _tickets.TryRemove(key, out _);

        var ticket = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        _tickets[ticket] = new UploadTicket(actor, nodeId, now + Lifetime);
        return ticket;
    }

    /// <summary>チケットを使用済みにして内容を返す。無効・期限切れ・使用済みならnull。</summary>
    public UploadTicket? Redeem(string ticket) =>
        _tickets.TryRemove(ticket, out var entry) && entry.ExpiresAt > time.GetUtcNow() ? entry : null;

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
