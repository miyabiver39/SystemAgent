using System.CommandLine;
using System.Net;
using SystemAgent.Cli;
using SystemAgent.Client;
using SystemAgent.Core.Contracts;
using SystemAgent.Core.Nodes;

// WebUIと同一の操作をWebAPI経由で提供するCLI（基本設計書 2.1節・9章）。業務ロジックは持たず、SystemAgent.ClientのApiClientだけを使う。

const string DefaultUrl = "http://localhost:5000";
const string DefaultSecretsDir = "/var/lib/systemagent/secrets";

// Windowsの既定コードページ(CP932等)では日本語が化けるため、出力は常にUTF-8にする
Console.OutputEncoding = System.Text.Encoding.UTF8;

var tokens = new FileTokenStore();

var urlOption = new Option<string?>("--url")
{
    Description = $"SystemAgentのURL（省略時: ログイン時のURL → 環境変数SYSTEMAGENT_URL → {DefaultUrl}）",
    Recursive = true,
};
var jsonOption = new Option<bool>("--json") { Description = "結果をJSONで出力する", Recursive = true };

var root = new RootCommand("SystemAgent CLI");
root.Options.Add(urlOption);
root.Options.Add(jsonOption);

// --- setup ---
var setupTokenOption = new Option<string?>("--token") { Description = "セットアップトークン（省略時はsetup-tokenファイルから読む）" };
var secretsDirOption = new Option<string>("--secrets-dir")
{
    Description = "秘密情報ディレクトリ",
    DefaultValueFactory = _ => DefaultSecretsDir,
};
var setupUserOption = new Option<string?>("--user", "-u") { Description = "作成する管理者のユーザー名（既定: admin）" };
var setup = new Command("setup", "初期セットアップ（緊急認証の管理者を作成）。サーバー上でroot権限で実行するとトークンを自動で読み込む。");
setup.Options.Add(setupTokenOption);
setup.Options.Add(secretsDirOption);
setup.Options.Add(setupUserOption);
setup.SetAction((p, ct) => Run(p, async api =>
{
    if (!(await api.GetSetupStatusAsync(ct)).Required)
    {
        Console.WriteLine("初期セットアップは完了済みです。");
        return;
    }

    var token = p.GetValue(setupTokenOption) ?? ReadSetupTokenFile(p.GetValue(secretsDirOption)!)
        ?? ConsoleUi.ReadSecret("セットアップトークン: ");
    var userName = p.GetValue(setupUserOption) ?? ConsoleUi.ReadLine("管理者ユーザー名: ", "admin");
    var password = ConsoleUi.ReadNewPassword(PasswordPolicy.MinLength);

    try
    {
        await api.CompleteSetupAsync(new SetupRequest(token, userName, password), ct);
    }
    catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
    {
        throw new CliException("セットアップトークンが正しくありません。");
    }
    Console.WriteLine($"初期セットアップが完了しました。`systemagent login --emergency -u {userName}` でログインできます。");
}));
root.Subcommands.Add(setup);

// --- login / logout / status / passwd ---
var loginUserOption = new Option<string?>("--user", "-u") { Description = "ユーザー名" };
var emergencyOption = new Option<bool>("--emergency") { Description = "緊急ログイン（ローカル認証。DB停止中も利用可能）" };
var login = new Command("login", "ログインする");
login.Options.Add(loginUserOption);
login.Options.Add(emergencyOption);
login.SetAction((p, ct) => Run(p, async api =>
{
    var userName = p.GetValue(loginUserOption) ?? ConsoleUi.ReadLine("ユーザー名: ");
    var password = ConsoleUi.ReadSecret("パスワード: ");
    var emergency = p.GetValue(emergencyOption);
    TokenResponse token;
    try
    {
        token = await api.LoginAsync(userName, password, emergency, ct);
    }
    catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
    {
        throw new CliException("ユーザー名またはパスワードが正しくありません。");
    }
    catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.ServiceUnavailable && !emergency)
    {
        throw new CliException("データベースに接続できません。--emergency を付けて緊急ログインしてください。");
    }
    tokens.Save(new Session(ResolveUrl(p), userName, token));
    Console.WriteLine($"{userName} としてログインしました（{AuthSourceLabel(token.AuthSource)}、有効期限 {token.ExpiresAt.ToLocalTime():yyyy-MM-dd HH:mm}）。");
}));
root.Subcommands.Add(login);

var logout = new Command("logout", "ログアウトする（保存したトークンを削除）");
logout.SetAction(_ =>
{
    tokens.Clear();
    Console.WriteLine("ログアウトしました。");
});
root.Subcommands.Add(logout);

var status = new Command("status", "接続先・DB接続状態・ログイン状態を表示する");
status.SetAction((p, ct) => Run(p, async api =>
{
    var health = await api.GetHealthAsync(ct);
    var setupRequired = (await api.GetSetupStatusAsync(ct)).Required;
    MeResponse? me = tokens.Load() is null ? null : await api.GetMeAsync(ct);
    if (p.GetValue(jsonOption))
    {
        ConsoleUi.WriteJson(new { url = ResolveUrl(p), health, setupRequired, me });
        return;
    }
    Console.WriteLine($"接続先      : {ResolveUrl(p)}");
    if (setupRequired) Console.WriteLine("初期設定    : 未実施（`sudo systemagent setup` を実行してください）");
    Console.WriteLine($"データベース: {(health.Database ? "接続中" : "接続不可（緊急ログインのみ利用可能）")}");
    Console.WriteLine($"ログイン    : {(me is null ? "未ログイン" : $"{me.UserName}（{AuthSourceLabel(me.AuthSource)}）")}");
}));
root.Subcommands.Add(status);

var passwd = new Command("passwd", "ログイン中のユーザーのパスワードを変更する");
passwd.SetAction((p, ct) => Run(p, async api =>
{
    var me = await api.GetMeAsync(ct);
    var password = ConsoleUi.ReadNewPassword(PasswordPolicy.MinLength);
    await api.ChangePasswordAsync(me.UserName, password, emergency: me.AuthSource == AuthSources.Emergency, ct);
    Console.WriteLine("パスワードを変更しました。");
}));
root.Subcommands.Add(passwd);

// --- node ---
var node = new Command("node", "ノード管理");
var nodeList = new Command("list", "登録ノードを一覧表示する");
nodeList.SetAction((p, ct) => Run(p, async api =>
{
    var nodes = await api.GetNodesAsync(ct);
    if (p.GetValue(jsonOption)) { ConsoleUi.WriteJson(nodes); return; }
    if (nodes.Count == 0) { Console.WriteLine("登録済みのノードはありません。"); return; }
    ConsoleUi.WriteTable(
        ["ID", "ホスト名", "IPアドレス", "OS", "役割", "登録日時"],
        nodes.Select(n => (IReadOnlyList<string>)
        [
            n.Id.ToString(), n.HostName, n.IpAddress, $"{n.Os.Distribution} {n.Os.Version} ({n.Os.Architecture})",
            n.Role.ToString(), n.RegisteredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
        ]));
}));
var hostOption = new Option<string>("--host") { Description = "ホスト名", Required = true };
var ipOption = new Option<string>("--ip") { Description = "IPアドレス", Required = true };
var distroOption = new Option<string>("--distro") { Description = "ディストリビューション（例: almalinux）", Required = true };
var versionOption = new Option<string>("--os-version") { Description = "OSバージョン（例: 9.8）", Required = true };
var archOption = new Option<string>("--arch") { Description = "アーキテクチャ", DefaultValueFactory = _ => "x86_64" };
var roleOption = new Option<NodeRole>("--role") { Description = "役割", DefaultValueFactory = _ => NodeRole.Managed };
var nodeRegister = new Command("register", "ノードを登録する");
foreach (var o in new Option[] { hostOption, ipOption, distroOption, versionOption, archOption, roleOption }) nodeRegister.Options.Add(o);
nodeRegister.SetAction((p, ct) => Run(p, async api =>
{
    NodeInfo created;
    try
    {
        created = await api.RegisterNodeAsync(new RegisterNodeRequest(
            p.GetValue(hostOption)!, p.GetValue(ipOption)!,
            new OsInfo(p.GetValue(distroOption)!, p.GetValue(versionOption)!, p.GetValue(archOption)!),
            p.GetValue(roleOption)), ct);
    }
    catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
    {
        throw new CliException("同じホスト名のノードが既に登録されています。");
    }
    if (p.GetValue(jsonOption)) ConsoleUi.WriteJson(created);
    else Console.WriteLine($"ノードを登録しました: {created.HostName} ({created.Id})");
}));
var nodeIdArgument = new Argument<Guid>("id") { Description = "ノードID（node listで確認）" };
var nodeDelete = new Command("delete", "ノードを削除する");
nodeDelete.Arguments.Add(nodeIdArgument);
nodeDelete.SetAction((p, ct) => Run(p, async api =>
{
    try
    {
        await api.DeleteNodeAsync(p.GetValue(nodeIdArgument), ct);
    }
    catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
    {
        throw new CliException("指定されたノードは存在しません。");
    }
    Console.WriteLine("ノードを削除しました。");
}));
node.Subcommands.Add(nodeList);
node.Subcommands.Add(nodeRegister);
node.Subcommands.Add(nodeDelete);
root.Subcommands.Add(node);

// --- user ---
var user = new Command("user", "ユーザー管理（通常認証のDBユーザー）");
var userList = new Command("list", "ユーザーを一覧表示する");
userList.SetAction((p, ct) => Run(p, async api =>
{
    var users = await api.GetUsersAsync(ct);
    if (p.GetValue(jsonOption)) { ConsoleUi.WriteJson(users); return; }
    if (users.Count == 0) { Console.WriteLine("ユーザーはまだ登録されていません。"); return; }
    ConsoleUi.WriteTable(["ユーザー名", "作成日時"],
        users.Select(u => (IReadOnlyList<string>)[u.UserName, u.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")]));
}));
var userNameArgument = new Argument<string>("name") { Description = "ユーザー名" };
var userCreate = new Command("create", "ユーザーを作成する（パスワードは対話入力）");
userCreate.Arguments.Add(userNameArgument);
userCreate.SetAction((p, ct) => Run(p, async api =>
{
    var password = ConsoleUi.ReadNewPassword(PasswordPolicy.MinLength);
    try
    {
        await api.CreateUserAsync(new CreateUserRequest(p.GetValue(userNameArgument)!, password), ct);
    }
    catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
    {
        throw new CliException("同じ名前のユーザーが既に存在します。");
    }
    Console.WriteLine($"ユーザー {p.GetValue(userNameArgument)} を作成しました。");
}));
var userDelete = new Command("delete", "ユーザーを削除する");
userDelete.Arguments.Add(userNameArgument);
userDelete.SetAction((p, ct) => Run(p, async api =>
{
    try
    {
        await api.DeleteUserAsync(p.GetValue(userNameArgument)!, ct);
    }
    catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
    {
        throw new CliException("指定されたユーザーは存在しません。");
    }
    Console.WriteLine($"ユーザー {p.GetValue(userNameArgument)} を削除しました。");
}));
user.Subcommands.Add(userList);
user.Subcommands.Add(userCreate);
user.Subcommands.Add(userDelete);
root.Subcommands.Add(user);

return await root.Parse(args).InvokeAsync();

string ResolveUrl(ParseResult p) =>
    p.GetValue(urlOption)
    ?? tokens.Load()?.Url
    ?? Environment.GetEnvironmentVariable("SYSTEMAGENT_URL")
    ?? DefaultUrl;

async Task<int> Run(ParseResult p, Func<ApiClient, Task> action)
{
    var url = ResolveUrl(p);
    using var http = new HttpClient { BaseAddress = new Uri(url.EndsWith('/') ? url : url + "/") };
    try
    {
        await action(new ApiClient(http, tokens));
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

static string? ReadSetupTokenFile(string secretsDir)
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

static string AuthSourceLabel(string authSource) => authSource == AuthSources.Emergency ? "緊急認証" : "通常認証";
