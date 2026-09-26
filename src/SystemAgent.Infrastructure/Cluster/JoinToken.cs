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
            return new JoinToken(payload.U, payload.F, payload.S);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new ArgumentException("参加トークンの形式が正しくありません。", ex);
        }
    }

    public static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private sealed record Payload(string? U, string? F, string? S);
}
