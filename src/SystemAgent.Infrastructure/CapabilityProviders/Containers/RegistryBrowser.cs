using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Core.Errors;

namespace SystemAgent.Infrastructure.CapabilityProviders.Containers;

/// <summary>
/// 登録済みレジストリの中のイメージ（リポジトリ・タグ）を OCI Distribution API で参照・削除する（ADR-026。ZOTを想定）。
/// 認証は登録済みの認証情報で、サーバーの要求に応じて Basic / Digest を使い分ける（htpasswd）。
/// TLS検証を無効にしたレジストリは、HTTPSで接続できなければHTTPで接続する。
/// HttpClient はレジストリ（認証情報）ごとに作って使い回す（要求のたびに作るとページ送りのたびに接続を張り直し、
/// TIME_WAIT のソケットが溜まる）。認証情報が変わったら作り直す。
/// </summary>
public sealed partial class RegistryBrowser : IDisposable
{
    private const int MaxPages = 50;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private readonly RegistryService _registries;
    private readonly Func<RegistryCredential, HttpMessageHandler> _handlerFactory;
    private readonly Lock _clientsLock = new();
    private readonly Dictionary<string, (RegistryCredential Credential, HttpClient Client)> _clients = new(StringComparer.OrdinalIgnoreCase);

    public RegistryBrowser(RegistryService registries) : this(registries, CreateHandler) { }

    /// <param name="handlerFactory">テスト用に通信を差し替える。</param>
    public RegistryBrowser(RegistryService registries, Func<RegistryCredential, HttpMessageHandler> handlerFactory)
    {
        _registries = registries;
        _handlerFactory = handlerFactory;
    }

    public async Task<IReadOnlyList<string>> ListRepositoriesAsync(string registry, CancellationToken cancellationToken = default)
    {
        var repositories = new List<string>();
        await foreach (var page in PagesAsync<CatalogPage>(Credential(registry), "v2/_catalog?n=1000", cancellationToken))
            repositories.AddRange(page.Repositories ?? []);
        return repositories.Order(StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<string>> ListTagsAsync(string registry, string repository, CancellationToken cancellationToken = default)
    {
        ValidateRepository(repository);
        var tags = new List<string>();
        await foreach (var page in PagesAsync<TagsPage>(Credential(registry), $"v2/{repository}/tags/list?n=1000", cancellationToken))
            tags.AddRange(page.Tags ?? []);
        return tags.Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// タグを削除する（OCI Distribution 1.1 のタグ指定削除。同じ内容を指す他のタグは残る）。
    /// タグ指定の削除に対応していないレジストリではエラーにする（ダイジェスト指定で削除すると他のタグまで消えるため自動では行わない）。
    /// </summary>
    public async Task DeleteTagAsync(string registry, string repository, string tag, CancellationToken cancellationToken = default)
    {
        ValidateRepository(repository);
        if (!ContainerNames.IsValidTag(tag)) throw new ArgumentException($"タグが不正です: {tag}");
        using var response = await SendAsync(Credential(registry), HttpMethod.Delete, $"v2/{repository}/manifests/{tag}", cancellationToken);
        if (response.IsSuccessStatusCode) return;
        throw response.StatusCode switch
        {
            HttpStatusCode.NotFound => new NotFoundException($"{repository}:{tag} はレジストリにありません。"),
            HttpStatusCode.MethodNotAllowed or HttpStatusCode.BadRequest => new RegistryRequestException(
                $"このレジストリはタグ指定の削除に対応していないか、削除が無効になっています（HTTP {(int)response.StatusCode}）。"),
            _ => await ErrorAsync(response, cancellationToken),
        };
    }

    private RegistryCredential Credential(string registry) =>
        _registries.Find(registry) ?? throw new NotFoundException($"レジストリ {registry} は登録されていません。");

    /// <summary>Link ヘッダ（rel="next"）をたどって全ページを返す。</summary>
    private async IAsyncEnumerable<T> PagesAsync<T>(RegistryCredential credential, string firstPage,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? path = firstPage;
        for (var page = 0; page < MaxPages && path is not null; page++)
        {
            using var response = await SendAsync(credential, HttpMethod.Get, path, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) throw new NotFoundException("レジストリに該当するリポジトリがありません。");
            if (!response.IsSuccessStatusCode) throw await ErrorAsync(response, cancellationToken);
            yield return (await response.Content.ReadFromJsonAsync<T>(cancellationToken))!;
            path = NextPage(response, credential.Registry);
        }
    }

    /// <summary>
    /// 次のページ（Link ヘッダの rel="next"）のパス。レジストリが返す値は信用せず、同じレジストリの /v2/ 配下のパスに限る。
    /// 別ホスト（http://169.254.169.254/ 等）・プロトコル相対（//host/）・.. を含むパスは、認証情報を付けて
    /// 別の宛先へ要求を送らされる（SSRF）おそれがあるため拒否する。
    /// </summary>
    public static string? NextPage(HttpResponseMessage response, string registry)
    {
        if (!response.Headers.TryGetValues("Link", out var links)) return null;
        var match = LinkNextPattern().Match(string.Join(',', links));
        if (!match.Success) return null;

        var link = match.Groups[1].Value.Trim();
        string pathAndQuery;
        if (link.StartsWith("//", StringComparison.Ordinal) || link.Contains('\\') || link.Any(char.IsControl))
            throw InvalidLink(link);
        if (Uri.TryCreate(link, UriKind.Absolute, out var absolute) && link.Contains("://", StringComparison.Ordinal))
        {
            if (absolute.Scheme is not ("https" or "http") || !string.Equals(absolute.Authority, registry, StringComparison.OrdinalIgnoreCase)
                || absolute.UserInfo.Length > 0)
                throw InvalidLink(link);
            pathAndQuery = absolute.PathAndQuery;
        }
        else
        {
            pathAndQuery = link;
        }

        var path = pathAndQuery.TrimStart('/');
        var pathOnly = path.Split('?', 2)[0];
        if (!path.StartsWith("v2/", StringComparison.Ordinal)
            || Uri.UnescapeDataString(pathOnly).Split('/').Any(segment => segment is "." or ".."))
            throw InvalidLink(link);
        return path;
    }

    private static RegistryRequestException InvalidLink(string link) =>
        new($"レジストリが不正な次ページの位置（Link ヘッダ）を返したため、一覧の取得を中止しました: {link[..Math.Min(link.Length, 200)]}");

    private HttpClient ClientFor(RegistryCredential credential)
    {
        lock (_clientsLock)
        {
            if (_clients.TryGetValue(credential.Registry, out var cached))
            {
                if (cached.Credential == credential) return cached.Client;
                // 認証情報・TLS設定が変わった
                cached.Client.Dispose();
            }
            var client = new HttpClient(_handlerFactory(credential)) { Timeout = RequestTimeout };
            _clients[credential.Registry] = (credential, client);
            return client;
        }
    }

    public void Dispose()
    {
        lock (_clientsLock)
        {
            foreach (var (_, client) in _clients.Values) client.Dispose();
            _clients.Clear();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(RegistryCredential credential, HttpMethod method, string path, CancellationToken cancellationToken)
    {
        var client = ClientFor(credential);
        try
        {
            return await client.SendAsync(new HttpRequestMessage(method, $"https://{credential.Registry}/{path}"), cancellationToken);
        }
        catch (HttpRequestException) when (!credential.TlsVerify)
        {
            // TLS検証を無効にしたレジストリはHTTPのこともある（podman の --tls-verify=false と同じ扱い）
        }
        catch (HttpRequestException ex)
        {
            throw new RegistryRequestException($"レジストリ {credential.Registry} に接続できません: {ex.Message}", unreachable: true);
        }

        try
        {
            return await client.SendAsync(new HttpRequestMessage(method, $"http://{credential.Registry}/{path}"), cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new RegistryRequestException($"レジストリ {credential.Registry} に接続できません: {ex.Message}", unreachable: true);
        }
    }

    private static async Task<Exception> ErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return new RegistryRequestException("レジストリの認証に失敗しました。登録しているユーザー名・パスワードを確認してください。");
        if (response.StatusCode == HttpStatusCode.Forbidden)
            return new RegistryRequestException("このユーザーにはレジストリのこの操作の権限がありません。");
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return new RegistryRequestException($"レジストリがエラーを返しました（HTTP {(int)response.StatusCode}）: {body[..Math.Min(body.Length, 500)]}");
    }

    private static HttpMessageHandler CreateHandler(RegistryCredential credential)
    {
        var handler = new SocketsHttpHandler
        {
            // サーバーの要求（WWW-Authenticate）に応じて Basic / Digest で認証する
            Credentials = new NetworkCredential(credential.Username, credential.Password),
            PreAuthenticate = true,
            // 使い回す接続も一定時間で張り直す（レジストリのアドレス変更・DNSの変化に追従する）
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            // 別ホストへのリダイレクトで認証情報を送らない
            AllowAutoRedirect = false,
        };
        if (!credential.TlsVerify)
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        return handler;
    }

    private static void ValidateRepository(string repository)
    {
        if (repository.Length > 255 || !RepositoryPattern().IsMatch(repository))
            throw new ArgumentException($"リポジトリ名が不正です: {repository}");
    }

    private sealed record CatalogPage([property: JsonPropertyName("repositories")] List<string>? Repositories);

    private sealed record TagsPage([property: JsonPropertyName("tags")] List<string>? Tags);

    // OCI Distribution Spec のリポジトリ名
    [GeneratedRegex(@"^[a-z0-9]+((\.|_|__|-+)[a-z0-9]+)*(/[a-z0-9]+((\.|_|__|-+)[a-z0-9]+)*)*$")]
    private static partial Regex RepositoryPattern();

    [GeneratedRegex(@"<([^>]+)>\s*;\s*rel=""?next""?")]
    private static partial Regex LinkNextPattern();
}
