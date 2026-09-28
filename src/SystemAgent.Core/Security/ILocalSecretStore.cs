namespace SystemAgent.Core.Security;

/// <summary>
/// ホスト固有鍵で暗号化されたローカル秘密情報（ADR-003）。DBに依存しない。
/// JWT署名鍵とローカル緊急認証ユーザーを保持する。
/// </summary>
public interface ILocalSecretStore
{
    /// <summary>緊急認証ユーザーが未作成（初期セットアップ未実施）か。ADR-015。</summary>
    bool IsSetupRequired { get; }

    SetupResult CompleteSetup(string setupToken, string userName, string password);

    byte[] GetJwtSigningKey();

    bool VerifyEmergencyUser(string userName, string password);

    bool ChangeEmergencyPassword(string userName, string newPassword);

    /// <summary>名前付きの秘密情報（CA秘密鍵・ノード秘密鍵など）。無ければnull。</summary>
    string? GetSecret(string name);

    /// <summary>名前付きの秘密情報を保存する。valueがnullなら削除。</summary>
    void SetSecret(string name, string? value);

    /// <summary>
    /// 名前付きの秘密情報を、読み込み→変更→保存の間に他の更新が割り込まないように更新する（変更結果がnullなら削除）。
    /// 同じ名前を扱う呼び出し元が複数あっても、ストア全体で排他するため更新が失われない。
    /// </summary>
    /// <returns>保存した値。</returns>
    string? UpdateSecret(string name, Func<string?, string?> update);
}

public enum SetupResult
{
    Completed,
    AlreadyCompleted,
    InvalidToken,
}
