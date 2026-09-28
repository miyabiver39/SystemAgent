using System.Security.Cryptography;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.JSInterop;
using SystemAgent.Client;
using SystemAgent.Core.Contracts;

namespace SystemAgent.Web.Client;

/// <summary>
/// WebUI(回線)ごとのアクセストークン。ブラウザのsessionStorageに暗号化して保持し、リロード後も維持する。
/// </summary>
public sealed class TokenStore(ProtectedSessionStorage storage, TimeProvider time) : ITokenProvider
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
                _loaded = true;
            }
            catch (CryptographicException)
            {
                // サーバーのデータ保護キーが変わった場合など。未ログイン扱いにする。
                _token = null;
                _loaded = true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or JSDisconnectedException)
            {
                // JS相互運用がまだ（プリレンダリング中・回線確立前）またはもう（回線切断後）使えない。
                // この時点では未ログインとして扱い、読み込み済みにはしないで次回呼ばれたときに読み直す
                return null;
            }
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
        try
        {
            await storage.DeleteAsync(StorageKey);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JSDisconnectedException)
        {
            // 回線切断後など。ブラウザ側のsessionStorageは閉じたタブとともに消える
        }
        Changed?.Invoke();
    }

    async ValueTask<string?> ITokenProvider.GetAccessTokenAsync() => (await GetAsync())?.AccessToken;

    // クリアするとレイアウトがログイン画面へ遷移させる
    ValueTask ITokenProvider.OnUnauthorizedAsync() => ClearAsync();
}
