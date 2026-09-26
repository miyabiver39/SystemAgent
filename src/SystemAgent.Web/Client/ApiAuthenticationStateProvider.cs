using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.IdentityModel.JsonWebTokens;
using SystemAgent.Web.Auth;

namespace SystemAgent.Web.Client;

public sealed class ApiAuthenticationStateProvider : AuthenticationStateProvider, IDisposable
{
    private static readonly AuthenticationState Anonymous = new(new ClaimsPrincipal(new ClaimsIdentity()));

    private readonly TokenStore _tokens;

    public ApiAuthenticationStateProvider(TokenStore tokens)
    {
        _tokens = tokens;
        _tokens.Changed += OnTokenChanged;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        if (await _tokens.GetAsync() is not { } token) return Anonymous;

        // 画面表示用にクレームを読むだけ。署名・有効期限の検証はAPI呼び出しのたびにサーバー側で行われる。
        var jwt = new JsonWebToken(token.AccessToken);
        return new AuthenticationState(new ClaimsPrincipal(
            new ClaimsIdentity(jwt.Claims, "jwt", AuthConstants.NameClaim, null)));
    }

    private void OnTokenChanged() => NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());

    public void Dispose() => _tokens.Changed -= OnTokenChanged;
}
