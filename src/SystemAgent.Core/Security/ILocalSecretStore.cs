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
}

public enum SetupResult
{
    Completed,
    AlreadyCompleted,
    InvalidToken,
}
