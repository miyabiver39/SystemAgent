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
var nodeOption = new Option<string?>("--node")
{
    Description = "操作対象のノード（ノード名またはID。省略時はこのノード）。コンテナ・NTP・ネットワーク等の操作に使える",
    Recursive = true,
};

var root = new RootCommand("SystemAgent CLI");
root.Options.Add(urlOption);
root.Options.Add(jsonOption);
root.Options.Add(nodeOption);

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

// --- node / cluster ---
var node = new Command("node", "登録ノード（登録は cluster token → join で行う）");
var nodeList = new Command("list", "登録ノードを一覧表示する");
nodeList.SetAction((p, ct) => Run(p, async api =>
{
    var nodes = await api.GetNodesAsync(ct);
    if (p.GetValue(jsonOption)) { ConsoleUi.WriteJson(nodes); return; }
    if (nodes.Count == 0) { Console.WriteLine("登録済みのノードはありません。"); return; }
    var self = (await api.GetClusterAsync(ct)).NodeId;
    ConsoleUi.WriteTable(
        ["ID", "ノード名", "接続先", "OS", "証明書の有効期限", ""],
        nodes.Select(n => (IReadOnlyList<string>)
        [
            n.Id.ToString(), n.HostName, $"{n.IpAddress}:{n.ClusterPort}", $"{n.Os.Distribution} {n.Os.Version} ({n.Os.Architecture})",
            Formatting.DateTime(n.CertificateNotAfter), n.Id == self ? "このノード" : "",
        ]));
}));
var nodeIdArgument = new Argument<Guid>("id") { Description = "ノードID（node listで確認）" };
var nodeDelete = new Command("delete", "ノードの登録を削除する（発行済み証明書は失効しない）");
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
node.Subcommands.Add(nodeDelete);
root.Subcommands.Add(node);

var cluster = new Command("cluster", "クラスタ（自己CA・ノード間通信）");
var clusterStatus = new Command("status", "このノードのクラスタ参加状態を表示する");
clusterStatus.SetAction((p, ct) => Run(p, async api =>
{
    var c = await api.GetClusterAsync(ct);
    if (p.GetValue(jsonOption)) { ConsoleUi.WriteJson(c); return; }
    Console.WriteLine($"ノード名    : {c.NodeName}（{c.AdvertiseAddress}:{c.ClusterPort}）");
    if (!c.Joined) { Console.WriteLine("状態        : 未参加（cluster init で初期化、または join で参加）"); return; }
    Console.WriteLine($"クラスタ    : {c.ClusterName}{(c.IsCa ? "（このノードがCA）" : "")}");
    Console.WriteLine($"ノードID    : {c.NodeId}");
    Console.WriteLine($"CA指紋      : {c.CaFingerprint}");
    Console.WriteLine($"証明書期限  : {Formatting.DateTime(c.CertificateNotAfter)}");
}));
cluster.Subcommands.Add(clusterStatus);
var clusterNameArgument = new Argument<string>("name") { Description = "クラスタ名（英数字・._-）" };
var clusterInit = new Command("init", "このノードをクラスタCAとして初期化する（クラスタの最初の1台で1回だけ）");
clusterInit.Arguments.Add(clusterNameArgument);
clusterInit.SetAction((p, ct) => Run(p, async api =>
{
    var c = await api.InitializeClusterAsync(p.GetValue(clusterNameArgument)!, ct);
    Console.WriteLine($"クラスタ '{c.ClusterName}' を初期化しました。CA指紋: {c.CaFingerprint}");
}));
cluster.Subcommands.Add(clusterInit);
var minutesOption = new Option<int>("--minutes") { Description = "有効期間（分）", DefaultValueFactory = _ => 60 };
var clusterToken = new Command("token", "ノード参加用のワンタイムトークンを発行する");
clusterToken.Options.Add(minutesOption);
clusterToken.SetAction((p, ct) => Run(p, async api =>
{
    var t = await api.CreateJoinTokenAsync(p.GetValue(minutesOption), ct);
    if (p.GetValue(jsonOption)) { ConsoleUi.WriteJson(t); return; }
    Console.Error.WriteLine($"有効期限 {Formatting.DateTime(t.ExpiresAt)}、接続先CA {t.CaUrl}。追加するノードで次を実行してください:");
    Console.WriteLine($"sudo systemagent join {t.Token}");
}));
cluster.Subcommands.Add(clusterToken);
root.Subcommands.Add(cluster);

var tokenArgument = new Argument<string>("token") { Description = "cluster token で発行した参加トークン" };
var join = new Command("join", "このノードをクラスタに参加させる（事前に setup と login が必要）");
join.Arguments.Add(tokenArgument);
join.SetAction((p, ct) => Run(p, async api =>
{
    var c = await api.JoinClusterAsync(p.GetValue(tokenArgument)!, ct);
    Console.WriteLine($"クラスタ '{c.ClusterName}' に参加しました（ノードID {c.NodeId}）。");
}));
root.Subcommands.Add(join);

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

// --- env ---
var refreshOption = new Option<bool>("--refresh") { Description = "キャッシュを使わず再検出する" };
var env = new Command("env", "このノードのOS情報と検出された管理対象ツールを表示する");
env.Options.Add(refreshOption);
env.SetAction((p, ct) => RunOnNode(p, async api =>
{
    var e = await api.GetEnvironmentAsync(p.GetValue(refreshOption), ct);
    if (p.GetValue(jsonOption)) { ConsoleUi.WriteJson(e); return; }
    Console.WriteLine($"ホスト名: {e.HostName}");
    Console.WriteLine($"OS      : {e.OsPrettyName}（ID={e.OsId}, VERSION_ID={e.OsVersion}, ID_LIKE={string.Join(' ', e.OsIdLike)}）");
    Console.WriteLine($"アーキ  : {e.Architecture}");
    Console.WriteLine("ツール  :");
    foreach (var tool in e.Tools) Console.WriteLine($"  {tool.Name} {tool.Version}");
}));
root.Subcommands.Add(env);

// --- container ---
var container = new Command("container", "コンテナ管理（このノード）");
var allOption = new Option<bool>("--all", "-a") { Description = "Podのインフラコンテナも表示する" };
var containerList = new Command("list", "コンテナを一覧表示する");
containerList.Options.Add(allOption);
containerList.SetAction((p, ct) => RunOnNode(p, async api =>
{
    var containers = (await api.GetContainersAsync(ct)).Where(c => p.GetValue(allOption) || !c.IsInfra).ToList();
    if (p.GetValue(jsonOption)) { ConsoleUi.WriteJson(containers); return; }
    if (containers.Count == 0) { Console.WriteLine("コンテナはありません。"); return; }
    ConsoleUi.WriteTable(["ID", "名前", "イメージ", "状態", "ステータス", "Pod"],
        containers.Select(c => (IReadOnlyList<string>)[Formatting.ShortId(c.Id), c.Name, c.Image, c.State.ToString(), c.Status, c.Pod ?? ""]));
}));
container.Subcommands.Add(containerList);
var containerIdArgument = new Argument<string>("container") { Description = "コンテナ名またはID" };
foreach (var (name, description) in new[] { ("start", "起動する"), ("stop", "停止する"), ("restart", "再起動する") })
{
    var command = new Command(name, $"コンテナを{description}");
    command.Arguments.Add(containerIdArgument);
    command.SetAction((p, ct) => RunOnNode(p, async api =>
    {
        await api.ContainerActionAsync(p.GetValue(containerIdArgument)!, name, ct);
        Console.WriteLine($"{p.GetValue(containerIdArgument)} を{description[..^2]}しました。");
    }));
    container.Subcommands.Add(command);
}
var containerRemove = new Command("rm", "コンテナを削除する（停止している必要がある）");
containerRemove.Arguments.Add(containerIdArgument);
containerRemove.SetAction((p, ct) => RunOnNode(p, async api =>
{
    await api.RemoveContainerAsync(p.GetValue(containerIdArgument)!, ct);
    Console.WriteLine($"{p.GetValue(containerIdArgument)} を削除しました。");
}));
container.Subcommands.Add(containerRemove);
var tailOption = new Option<int>("--tail", "-n") { Description = "末尾から表示する行数", DefaultValueFactory = _ => 200 };
var containerLogs = new Command("logs", "コンテナのログを表示する");
containerLogs.Arguments.Add(containerIdArgument);
containerLogs.Options.Add(tailOption);
containerLogs.SetAction((p, ct) => RunOnNode(p, async api =>
    Console.Write((await api.GetContainerLogsAsync(p.GetValue(containerIdArgument)!, p.GetValue(tailOption), ct)).Logs)));
container.Subcommands.Add(containerLogs);
root.Subcommands.Add(container);

// --- pod ---
var pod = new Command("pod", "Pod管理（Podmanのみ）");
var podList = new Command("list", "Podを一覧表示する");
podList.SetAction((p, ct) => RunOnNode(p, async api =>
{
    var pods = await api.GetPodsAsync(ct);
    if (p.GetValue(jsonOption)) { ConsoleUi.WriteJson(pods); return; }
    if (pods.Count == 0) { Console.WriteLine("Podはありません（DockerではPodは利用できません）。"); return; }
    ConsoleUi.WriteTable(["ID", "名前", "状態", "コンテナ数"],
        pods.Select(x => (IReadOnlyList<string>)[Formatting.ShortId(x.Id), x.Name, x.Status, x.ContainerCount.ToString()]));
}));
pod.Subcommands.Add(podList);
root.Subcommands.Add(pod);

// --- image ---
var image = new Command("image", "コンテナイメージ管理（このノード）");
var imageList = new Command("list", "イメージを一覧表示する");
imageList.SetAction((p, ct) => RunOnNode(p, async api =>
{
    var images = await api.GetImagesAsync(ct);
    if (p.GetValue(jsonOption)) { ConsoleUi.WriteJson(images); return; }
    if (images.Count == 0) { Console.WriteLine("イメージはありません。"); return; }
    ConsoleUi.WriteTable(["ID", "タグ", "サイズ", "作成日時"],
        images.Select(i => (IReadOnlyList<string>)
        [
            Formatting.ShortId(i.Id), i.Tags.Count == 0 ? "<none>" : string.Join(", ", i.Tags),
            Formatting.Bytes(i.SizeBytes),
            Formatting.DateTime(i.CreatedAt),
        ]));
}));
image.Subcommands.Add(imageList);
var imageArgument = new Argument<string>("image") { Description = "イメージ名（例: registry.local:5000/app:1.0）またはID" };
var imagePull = new Command("pull", "レジストリからイメージを取得する");
imagePull.Arguments.Add(imageArgument);
imagePull.SetAction((p, ct) => RunOnNode(p, async api =>
{
    Console.Error.WriteLine($"{p.GetValue(imageArgument)} を取得しています...");
    await api.PullImageAsync(p.GetValue(imageArgument)!, ct);
    Console.WriteLine("取得しました。");
}));
image.Subcommands.Add(imagePull);
var imageRemove = new Command("rm", "イメージを削除する");
imageRemove.Arguments.Add(imageArgument);
imageRemove.SetAction((p, ct) => RunOnNode(p, async api =>
{
    await api.RemoveImageAsync(p.GetValue(imageArgument)!, ct);
    Console.WriteLine($"{p.GetValue(imageArgument)} を削除しました。");
}));
image.Subcommands.Add(imageRemove);
var archiveArgument = new Argument<FileInfo>("archive") { Description = "イメージアーカイブ（podman save / docker save で作成したtar）" };
var imageImport = new Command("import", "イメージアーカイブを取り込む（エアギャップ環境向け）");
imageImport.Arguments.Add(archiveArgument);
imageImport.SetAction((p, ct) => RunOnNode(p, async api =>
{
    var archive = p.GetValue(archiveArgument)!;
    if (!archive.Exists) throw new CliException($"ファイルが見つかりません: {archive.FullName}");
    Console.Error.WriteLine($"{archive.Name}（{Formatting.Bytes(archive.Length)}）を送信しています...");
    await using var stream = archive.OpenRead();
    Console.WriteLine((await api.ImportImageAsync(stream, archive.Name, ct)).Output);
}));
image.Subcommands.Add(imageImport);
root.Subcommands.Add(image);

// --- ntp ---
var ntp = new Command("ntp", "時刻同期（NTP）設定（このノード）");
var ntpStatus = new Command("status", "同期状態とNTPサーバーを表示する");
ntpStatus.SetAction((p, ct) => RunOnNode(p, async api =>
{
    var n = await api.GetNtpAsync(ct);
    if (p.GetValue(jsonOption)) { ConsoleUi.WriteJson(n); return; }
    var s = n.Status;
    Console.WriteLine($"実装        : {n.Implementation.Name}（テンプレート {n.Implementation.TemplateId}、設定 {n.Implementation.ConfigFile}）");
    Console.WriteLine($"同期状態    : {(s.Synchronized ? "同期済み" : "未同期")}");
    Console.WriteLine($"同期先      : {s.CurrentSource ?? "-"}{(s.Stratum is { } st ? $"（stratum {st}）" : "")}");
    if (s.OffsetSeconds is not null) Console.WriteLine($"時刻のずれ  : {Formatting.ClockOffset(s.OffsetSeconds)}");
    Console.WriteLine($"設定サーバー: {(s.ConfiguredServers.Count == 0 ? "-" : string.Join(", ", s.ConfiguredServers))}");
    if (s.Sources.Count > 0)
    {
        Console.WriteLine();
        ConsoleUi.WriteTable(["ソース", "状態", "stratum", "オフセット"],
            s.Sources.Select(x => (IReadOnlyList<string>)
                [x.Address, x.State.ToString(), x.Stratum?.ToString() ?? "", Formatting.Milliseconds(x.OffsetSeconds)]));
    }
}));
ntp.Subcommands.Add(ntpStatus);
var serversArgument = new Argument<string[]>("servers") { Description = "NTPサーバー（ホスト名またはIPアドレス、複数指定可）", Arity = ArgumentArity.OneOrMore };
var ntpSet = new Command("set", "参照するNTPサーバーを置き換える（サービスを再起動して反映。失敗時は元に戻す）");
ntpSet.Arguments.Add(serversArgument);
ntpSet.SetAction((p, ct) => RunOnNode(p, async api =>
{
    await api.SetNtpServersAsync(p.GetValue(serversArgument)!, ct);
    Console.WriteLine($"NTPサーバーを設定しました: {string.Join(", ", p.GetValue(serversArgument)!)}");
}));
ntp.Subcommands.Add(ntpSet);
var ntpSync = new Command("sync", "今すぐ時刻を合わせる");
ntpSync.SetAction((p, ct) => RunOnNode(p, async api =>
{
    await api.SyncNtpAsync(ct);
    Console.WriteLine("時刻同期を実行しました。");
}));
ntp.Subcommands.Add(ntpSync);
root.Subcommands.Add(ntp);

// --- network ---
var network = new Command("network", "ホストネットワーク（このノード。現在は参照のみ）");
var networkShow = new Command("show", "インターフェース・経路・DNSを表示する");
networkShow.Options.Add(allOption);
networkShow.SetAction((p, ct) => RunOnNode(p, async api =>
{
    var n = await api.GetNetworkAsync(ct);
    if (p.GetValue(jsonOption)) { ConsoleUi.WriteJson(n); return; }
    var hidden = n.Interfaces.Where(i => i.ContainerNetwork || i.Kind == SystemAgent.Core.CapabilityProviders.InterfaceKind.Loopback).Select(i => i.Name).ToHashSet();
    var interfaces = n.Interfaces.Where(i => p.GetValue(allOption) || (!i.ContainerNetwork && i.Kind != SystemAgent.Core.CapabilityProviders.InterfaceKind.Loopback)).ToList();
    ConsoleUi.WriteTable(["名前", "種別", "状態", "MTU", "MAC", "アドレス"],
        interfaces.Select(i => (IReadOnlyList<string>)
        [
            i.Name + (i.Master is { } m ? $" (@{m})" : ""), i.Kind.ToString(), i.State, i.Mtu?.ToString() ?? "", i.MacAddress ?? "",
            string.Join(", ", i.Addresses.Where(a => !a.Address.StartsWith("fe80")).Select(a => a.ToString() + (a.Dynamic ? " (DHCP)" : ""))),
        ]));
    Console.WriteLine();
    ConsoleUi.WriteTable(["宛先", "ゲートウェイ", "デバイス", "プロトコル"],
        n.Routes.Where(r => p.GetValue(allOption) || (!r.Destination.StartsWith("fe80") && !hidden.Contains(r.Device ?? "")))
            .Select(r => (IReadOnlyList<string>)[r.Destination, r.Gateway ?? "", r.Device ?? "", r.Protocol ?? ""]));
    Console.WriteLine();
    Console.WriteLine($"DNSサーバー : {(n.DnsServers.Count == 0 ? "-" : string.Join(", ", n.DnsServers))}");
    Console.WriteLine($"検索ドメイン: {(n.SearchDomains.Count == 0 ? "-" : string.Join(", ", n.SearchDomains))}");
    if (!p.GetValue(allOption)) Console.Error.WriteLine("（ループバックとコンテナ用のインターフェースは省略。--all で表示）");
}));
network.Subcommands.Add(networkShow);
root.Subcommands.Add(network);

return await root.Parse(args).InvokeAsync();

string ResolveUrl(ParseResult p) =>
    p.GetValue(urlOption)
    ?? tokens.Load()?.Url
    ?? Environment.GetEnvironmentVariable("SYSTEMAGENT_URL")
    ?? DefaultUrl;

Task<int> RunOnNode(ParseResult p, Func<ApiClient, Task> action) => Run(p, async api =>
{
    if (p.GetValue(nodeOption) is not { Length: > 0 } target)
    {
        await action(api);
        return;
    }
    var nodes = await api.GetNodesAsync();
    var node = nodes.FirstOrDefault(n => n.Id.ToString() == target || n.HostName == target)
        ?? throw new CliException($"ノード '{target}' は登録されていません（node list で確認してください）。");
    await action(api.ForNode(node.Id));
});

async Task<int> Run(ParseResult p, Func<ApiClient, Task> action)
{
    var url = ResolveUrl(p);
    using var http = new HttpClient { BaseAddress = new Uri(url.EndsWith('/') ? url : url + "/"), Timeout = ApiClient.HttpTimeout };
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
