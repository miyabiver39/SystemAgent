using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SystemAgent.Infrastructure.Cluster;

/// <summary>
/// ノード参加トークン。CAノードの接続先・CA証明書の指紋・ワンタイムの秘密値を1つの文字列にまとめる。
/// 指紋があるため、参加前（証明書を持たない状態）でもCAノードへの接続を中間者攻撃から守れる。
/// </summary>
public sealed record JoinToken(string CaUrl, string CaFingerprint, string Secret)
{
    private const string Prefix = "SAJ1.";

    public string Encode() =>
        Prefix + Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new Payload(CaUrl, CaFingerprint, Secret)));

    public static JoinToken Decode(string token)
    {
        try
        {
            if (!token.StartsWith(Prefix, StringComparison.Ordinal)) throw new FormatException();
            var payload = JsonSerializer.Deserialize<Payload>(Base64Url.DecodeFromChars(token.AsSpan(Prefix.Length)))!;
            if (payload.U is null || payload.F is null || payload.S is null) throw new FormatException();
            return new JoinToken(ValidateCaUrl(payload.U), payload.F, payload.S);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new ArgumentException("参加トークンの形式が正しくありません。", ex);
        }
    }

    /// <summary>
    /// CAノードの接続先は https://ホスト:ポート だけを受け付ける（CreateJoinTokenAsync が作る形）。
    /// 細工したトークンで file:// やパス付きの任意のURLへ要求を送らせないため。
    /// </summary>
    public static string ValidateCaUrl(string caUrl)
    {
        if (!Uri.TryCreate(caUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(uri.Host)
            || uri.UserInfo.Length > 0
            || uri.AbsolutePath != "/"
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0
            || uri.Port is < 1 or > 65535)
        {
            throw new ArgumentException($"参加トークンのCAノードの接続先が不正です（https://ホスト:ポート の形式のみ）: {caUrl}");
        }
        return uri.GetLeftPart(UriPartial.Authority);
    }

    public static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private sealed record Payload(string? U, string? F, string? S);
}
