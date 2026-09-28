using System.Text.Json;
using System.Text.Json.Serialization;
using SystemAgent.Core.Security;

namespace SystemAgent.Infrastructure.Security;

/// <summary>
/// ローカルの暗号化シークレット（ADR-003）に、名前を付けてJSONで値を保存する。
/// HA設定・レジストリの認証情報・デプロイ定義など、秘密情報を含み、中央DBが止まっていても使う設定の保存に使う。
/// 列挙型は文字列で保存する（数値で保存された値も読める）。
/// 排他は ILocalSecretStore 側で行うため、同じ名前に対してインスタンスが複数あっても更新は失われない。
/// </summary>
public sealed class SecretJsonStore<T>(ILocalSecretStore secrets, string name) where T : class
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>保存されていなければnull。</summary>
    public T? Load() => Deserialize(secrets.GetSecret(name));

    public void Save(T value) => secrets.SetSecret(name, JsonSerializer.Serialize(value, Json));

    /// <summary>読み込み→変更→保存を排他して行う（同時に更新しても変更が失われないように）。</summary>
    public T Update(Func<T?, T> change)
    {
        T? updated = null;
        secrets.UpdateSecret(name, json =>
        {
            updated = change(Deserialize(json));
            return JsonSerializer.Serialize(updated, Json);
        });
        return updated!;
    }

    private static T? Deserialize(string? json) => json is null ? null : JsonSerializer.Deserialize<T>(json, Json);
}
