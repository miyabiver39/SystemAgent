using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SystemAgent.Core.Contracts;
using SystemAgent.Core.Security;

namespace SystemAgent.Web.Auth;

public static class AuthConstants
{
    public const string Issuer = "SystemAgent";
    public const string Audience = "SystemAgent";
    public const string AuthSourceClaim = "auth_source";
    public const string NameClaim = "name";
}

/// <summary>通常認証(DB)と緊急認証(ローカル)は同じ署名鍵を使い、auth_sourceクレーム(AuthSources)で区別する。</summary>
public sealed class JwtTokenIssuer(ILocalSecretStore secrets, IConfiguration configuration, TimeProvider time)
{
    private readonly JsonWebTokenHandler _handler = new();

    public TokenResponse Issue(string userName, string authSource)
    {
        var now = time.GetUtcNow();
        var expires = now.AddMinutes(configuration.GetValue("Auth:TokenLifetimeMinutes", 480));

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = AuthConstants.Issuer,
            Audience = AuthConstants.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, userName),
                new Claim(AuthConstants.NameClaim, userName),
                new Claim(AuthConstants.AuthSourceClaim, authSource),
            ]),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(secrets.GetJwtSigningKey()), SecurityAlgorithms.HmacSha256),
        });

        return new TokenResponse(token, expires, authSource);
    }

    public static TokenValidationParameters CreateValidationParameters(ILocalSecretStore secrets) => new()
    {
        ValidIssuer = AuthConstants.Issuer,
        ValidAudience = AuthConstants.Audience,
        IssuerSigningKey = new SymmetricSecurityKey(secrets.GetJwtSigningKey()),
        NameClaimType = AuthConstants.NameClaim,
        ClockSkew = TimeSpan.FromMinutes(1),
    };
}
