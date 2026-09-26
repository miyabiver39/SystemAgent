using System.Security.Cryptography;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using SystemAgent.Web.Auth;

namespace SystemAgent.Web.Client;

/// <summary>
/// WebUI(回線)ごとのアクセストークン。ブラウザのsessionStorageに暗号化して保持し、リロード後も維持する。
/// </summary>
public sealed class TokenStore(ProtectedSessionStorage storage, TimeProvider time)
{
    private const string StorageKey = "systemagent.token";

    private TokenResponse? _token;
    private bool _loaded;

    public event Action? Changed;

    public async ValueTask<TokenResponse?> GetAsync()
    {
        if (!_loaded)
        {
            try
            {
                var result = await storage.GetAsync<TokenResponse>(StorageKey);
                _token = result.Success ? result.Value : null;
            }
            catch (CryptographicException)
            {
                // サーバーのデータ保護キーが変わった場合など。未ログイン扱いにする。
                _token = null;
            }
            _loaded = true;
        }

        if (_token is not null && _token.ExpiresAt <= time.GetUtcNow())
        {
            await ClearAsync();
        }
        return _token;
    }

    public async ValueTask SetAsync(TokenResponse token)
    {
        _token = token;
        _loaded = true;
        await storage.SetAsync(StorageKey, token);
        Changed?.Invoke();
    }

    public async ValueTask ClearAsync()
    {
        _token = null;
        _loaded = true;
        await storage.DeleteAsync(StorageKey);
        Changed?.Invoke();
    }
}
