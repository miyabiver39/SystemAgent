namespace SystemAgent.Core.Security;

/// <summary>
/// ホスト固有鍵で暗号化されたローカル秘密情報（ADR-003）。DBに依存しない。
/// JWT署名鍵とローカル緊急認証ユーザーを保持する。
/// </summary>
public interface ILocalSecretStore
{
    byte[] GetJwtSigningKey();

    bool VerifyEmergencyUser(string userName, string password);

    bool ChangeEmergencyPassword(string userName, string newPassword);
}
