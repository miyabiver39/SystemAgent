using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Core.Contracts;
using SystemAgent.Core.Security;
using SystemAgent.Infrastructure.Security;
using SystemAgent.Core.Errors;

namespace SystemAgent.Infrastructure.CapabilityProviders.Containers;

/// <summary>レジストリ名（ホスト[:ポート]）の検証と、イメージ参照からのレジストリ判定。</summary>
public static partial class RegistryName
{
    public const string DockerHub = "docker.io";

    /// <summary>小文字に正規化して返す。Docker Hubの別名は docker.io にそろえる。</summary>
    public static string Validate(string registry)
    {
        var value = registry.Trim().ToLowerInvariant();
        if (value.Length > 253 || !Pattern().IsMatch(value))
            throw new ArgumentException($"レジストリの指定が不正です（ホスト名[:ポート]で指定してください）: {registry}");
        return value is "index.docker.io" or "registry-1.docker.io" ? DockerHub : value;
    }

    /// <summary>
    /// イメージ参照のレジストリ部分。先頭要素に "." か ":" を含むか localhost ならレジストリ、それ以外は Docker Hub
    /// （podman/docker と同じ解釈。podman の短縮名検索は考慮しない）。
    /// </summary>
    public static string FromImage(string image)
    {
        var slash = image.IndexOf('/');
        if (slash < 0) return DockerHub;
        var first = image[..slash];
        return first.Contains('.') || first.Contains(':') || first == "localhost" ? Validate(first) : DockerHub;
    }

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9.-]*[a-z0-9])?(:[0-9]{1,5})?$")]
    private static partial Regex Pattern();
}

/// <summary>登録済みレジストリの認証情報（サーバー内でのみ使う。APIでは返さない）。</summary>
/// <param name="TlsVerify">false なら証明書を検証しない（自己署名証明書・HTTPのレジストリ）。導入前の登録はtrue。</param>
public sealed record RegistryCredential(string Registry, string Username, string Password, DateTimeOffset UpdatedAt, bool TlsVerify = true);

/// <summary>
/// コンテナレジストリの認証情報（ADR-023）。パスワードはローカルの暗号化シークレットに保存し、APIでは返さない。
/// Podman の認証ファイルは /run 配下で再起動により消えるため、pull / push の直前に毎回ログインし直す。
/// </summary>
public sealed class RegistryService(ILocalSecretStore secrets, ILogger<RegistryService> logger)
{
    private readonly SecretJsonStore<List<RegistryCredential>> _store = new(secrets, "container.registries");

    public IReadOnlyList<RegistryView> List() =>
        Load().OrderBy(c => c.Registry).Select(c => new RegistryView(c.Registry, c.Username, c.UpdatedAt, c.TlsVerify)).ToList();

    /// <summary>登録済みの認証情報。未登録ならnull。</summary>
    public RegistryCredential? Find(string registry)
    {
        registry = RegistryName.Validate(registry);
        return Load().FirstOrDefault(c => c.Registry == registry);
    }

    /// <summary>
    /// ログインして成功したら保存する（誤った認証情報は保存しない）。パスワード省略時は保存済みの値を使う。
    /// </summary>
    public async Task<string> SaveAsync(IContainerRuntimeProvider runtime, string registry, string username, string? password,
        bool tlsVerify = true, CancellationToken cancellationToken = default)
    {
        registry = RegistryName.Validate(registry);
        if (string.IsNullOrEmpty(password))
        {
            password = Load().FirstOrDefault(c => c.Registry == registry)?.Password
                ?? throw new ArgumentException("パスワードを指定してください。");
        }
        await runtime.LoginAsync(registry, username, password, tlsVerify, cancellationToken);
        Update(list => [.. list.Where(c => c.Registry != registry), new RegistryCredential(registry, username, password, DateTimeOffset.UtcNow, tlsVerify)]);
        return registry;
    }

    /// <summary>ログアウトして認証情報を削除する。登録されていなければfalse。</summary>
    public async Task<bool> RemoveAsync(IContainerRuntimeProvider runtime, string registry, CancellationToken cancellationToken = default)
    {
        registry = RegistryName.Validate(registry);
        if (Load().All(c => c.Registry != registry)) return false;
        try
        {
            await runtime.LogoutAsync(registry, cancellationToken);
        }
        catch (CommandFailedException ex)
        {
            // 再起動で認証ファイルが消えている等、ログインしていない場合も削除は続ける
            logger.LogInformation("レジストリ {Registry} のログアウトに失敗しました（削除は続行）: {Error}", registry, ex.StandardError.Trim());
        }
        Update(list => [.. list.Where(c => c.Registry != registry)]);
        return true;
    }

    /// <summary>
    /// イメージのレジストリに認証情報が登録されていればログインする。登録がなければ何もしない（匿名でpull）。
    /// </summary>
    /// <returns>そのレジストリの証明書を検証するか（未登録ならtrue）。pull / push に渡す。</returns>
    public async Task<bool> EnsureLoginAsync(IContainerRuntimeProvider runtime, string image, CancellationToken cancellationToken = default)
    {
        var registry = RegistryName.FromImage(image);
        if (Load().FirstOrDefault(c => c.Registry == registry) is not { } credential) return true;
        await runtime.LoginAsync(credential.Registry, credential.Username, credential.Password, credential.TlsVerify, cancellationToken);
        return credential.TlsVerify;
    }

    private List<RegistryCredential> Load() => _store.Load() ?? [];

    private void Update(Func<List<RegistryCredential>, List<RegistryCredential>> change) => _store.Update(current => change(current ?? []));
}
