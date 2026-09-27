using System.CommandLine;
using System.Net;
using SystemAgent.Client;
using SystemAgent.Core.Contracts;

namespace SystemAgent.Cli;

/// <summary>
/// 全コマンドに共通する部分: 共通オプション（--url / --json / --node）、接続先の決定、ノード転送、エラーの表示。
/// 各コマンドは Run / RunOnNode にAPI呼び出しを渡すだけにし、例外は終了コード1とメッセージに変換する。
/// </summary>
internal sealed class CliContext
{
    public const string DefaultUrl = "http://localhost:5000";
    public const string DefaultSecretsDir = "/var/lib/systemagent/secrets";

    public FileTokenStore Tokens { get; } = new();

    public Option<string?> UrlOption { get; } = new("--url")
    {
        Description = $"SystemAgentのURL（省略時: ログイン時のURL → 環境変数SYSTEMAGENT_URL → {DefaultUrl}）",
        Recursive = true,
    };

    public Option<bool> JsonOption { get; } = new("--json") { Description = "結果をJSONで出力する", Recursive = true };

    public Option<string?> NodeOption { get; } = new("--node")
    {
        Description = "操作対象のノード（ノード名またはID。省略時はこのノード）。コンテナ・NTP・ネットワーク等の操作に使える",
        Recursive = true,
    };

    public RootCommand CreateRootCommand()
    {
        var root = new RootCommand("SystemAgent CLI");
        root.Options.Add(UrlOption);
        root.Options.Add(JsonOption);
        root.Options.Add(NodeOption);
        return root;
    }

    public string ResolveUrl(ParseResult p) =>
        p.GetValue(UrlOption)
        ?? Tokens.Load()?.Url
        ?? Environment.GetEnvironmentVariable("SYSTEMAGENT_URL")
        ?? LocalUrlFromConfig()
        ?? DefaultUrl;

    /// <summary>--node が指定されていれば、そのノードへ転送するクライアントで実行する。</summary>
    public Task<int> RunOnNode(ParseResult p, Func<ApiClient, Task> action) => Run(p, async api =>
    {
        if (p.GetValue(NodeOption) is not { Length: > 0 } target)
        {
            await action(api);
            return;
        }
        var nodes = await api.GetNodesAsync();
        var node = nodes.FirstOrDefault(n => n.Id.ToString() == target || n.HostName == target)
            ?? throw new CliException($"ノード '{target}' は登録されていません（node list で確認してください）。");
        await action(api.ForNode(node.Id));
    });

    /// <summary>APIを呼び出し、失敗は「エラー: ...」の表示と終了コード1にする。</summary>
    public async Task<int> Run(ParseResult p, Func<ApiClient, Task> action)
    {
        var url = ResolveUrl(p);
        using var http = new HttpClient { BaseAddress = new Uri(url.EndsWith('/') ? url : url + "/"), Timeout = ApiClient.HttpTimeout };
        try
        {
            await action(new ApiClient(http, Tokens));
            return 0;
        }
        catch (CliException ex)
        {
            Console.Error.WriteLine($"エラー: {ex.Message}");
            return 1;
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            Console.Error.WriteLine("エラー: ログインしていないか、トークンの有効期限が切れています。`systemagent login` を実行してください。");
            return 1;
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.NotImplemented)
        {
            Console.Error.WriteLine($"エラー: このノードでは利用できません。{ex.Message}");
            return 1;
        }
        catch (ApiException ex)
        {
            Console.Error.WriteLine($"エラー: {ex.Message}");
            return 1;
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"エラー: {url} に接続できません（{ex.Message}）。--url を確認してください。");
            return 1;
        }
    }

    /// <summary>このノードの /etc/systemagent/systemagent.json の Urls から、ローカルの接続先を決める（ポートを変更している場合のため）。</summary>
    private static string? LocalUrlFromConfig()
    {
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText("/etc/systemagent/systemagent.json"));
            if (!json.RootElement.TryGetProperty("Urls", out var urls) || urls.GetString() is not { Length: > 0 } value) return null;
            var first = new Uri(value.Split(';')[0].Replace("://+", "://localhost").Replace("://*", "://localhost").Replace("://0.0.0.0", "://localhost"));
            return $"{first.Scheme}://localhost:{first.Port}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or UriFormatException)
        {
            return null;
        }
    }

    public static string? ReadSetupTokenFile(string secretsDir)
    {
        try
        {
            return File.ReadAllText(Path.Combine(secretsDir, "setup-token")).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string AuthSourceLabel(string authSource) => authSource == AuthSources.Emergency ? "緊急認証" : "通常認証";
}
