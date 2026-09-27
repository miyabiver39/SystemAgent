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

// --- db ---
var db = new Command("db", "このノードのDB接続設定（接続情報はホスト固有鍵で暗号化して保存）");
var dbStatus = new Command("status", "DB接続設定と接続可否・未適用のマイグレーションを表示する");
dbStatus.SetAction((p, ct) => RunOnNode(p, async api =>
{
    var d = await api.GetDatabaseAsync(ct);
    if (p.GetValue(jsonOption)) { ConsoleUi.WriteJson(d); return; }
    var source = d.Source switch { "Secret" => "暗号化して保存済み", "Configuration" => "設定ファイル", _ => "未設定" };
    Console.WriteLine($"設定元      : {source}");
    if (d.Server is not null) Console.WriteLine($"接続先      : {d.User}@{d.Server}:{d.Port}/{d.Database}");
    Console.WriteLine($"接続        : {(d.Reachable ? "OK" : $"NG（{d.Error}）")}");
    if (d.PendingMigrations.Count > 0) Console.WriteLine($"未適用      : {string.Join(", ", d.PendingMigrations)}（systemagent db migrate で適用）");
}));
db.Subcommands.Add(dbStatus);
var dbServerOption = new Option<string>("--server") { Description = "DBサーバー（VIPを使う場合はVIP）", Required = true };
var dbPortOption = new Option<int>("--port") { Description = "ポート", DefaultValueFactory = _ => 3306 };
var dbNameOption = new Option<string>("--database") { Description = "データベース名", DefaultValueFactory = _ => "systemagent" };
var dbUserOption = new Option<string>("--user") { Description = "ユーザー名", DefaultValueFactory = _ => "systemagent" };
var dbSet = new Command("set", "DB接続設定を変更する（パスワードは対話入力。接続できた場合のみ保存し、テーブルを作成・更新する）");
foreach (var o in new Option[] { dbServerOption, dbPortOption, dbNameOption, dbUserOption }) dbSet.Options.Add(o);
dbSet.SetAction((p, ct) => RunOnNode(p, async api =>
{
    var password = ConsoleUi.ReadSecret("DBパスワード: ");
    await api.SetDatabaseAsync(new SetDatabaseRequest(
        p.GetValue(dbServerOption)!, p.GetValue(dbPortOption), p.GetValue(dbNameOption)!, p.GetValue(dbUserOption)!, password), ct);
    Console.WriteLine("DB接続設定を保存しました。");
}));
db.Subcommands.Add(dbSet);
var dbMigrate = new Command("migrate", "未適用のマイグレーション（テーブル作成・変更）を適用する");
dbMigrate.SetAction((p, ct) => RunOnNode(p, async api =>
{
    await api.MigrateDatabaseAsync(ct);
    Console.WriteLine("マイグレーションを適用しました。");
}));
db.Subcommands.Add(dbMigrate);
root.Subcommands.Add(db);

// --- ha ---
var haCommand = new Command("ha", "Keepalived と DBの昇格・降格（冗長化）");
var haStatus = new Command("status", "VRRPの状態・DBの役割・HA設定を表示する");
haStatus.SetAction((p, ct) => RunOnNode(p, async api =>
{
    var h = await api.GetHaAsync(ct);
    if (p.GetValue(jsonOption)) { ConsoleUi.WriteJson(h); return; }
    Console.WriteLine($"VRRP状態    : {h.State}{(h.Since is { } since ? $"（{Formatting.DateTime(since)} から）" : "")}");
    if (h.RejoinPending) Console.WriteLine("復帰待ち    : レプリカとしての再参加が承認待ちです（systemagent ha rejoin で承認）");
    if (h.Db is { } db)
    {
        var role = db.IsReplica
            ? $"レプリカ（{db.SourceHost}:{db.SourcePort}、複製{(db.ReplicationRunning ? "中" : "停止")}、遅延 {db.SecondsBehindSource?.ToString() ?? "-"} 秒）"
            : "レプリカではない";
        Console.WriteLine(db.Reachable
            ? $"ローカルDB  : {(db.ReadOnly ? "読み取り専用" : "書き込み可")}、{role}"
            : $"ローカルDB  : 接続不可（{db.Error}）");
        if (db.LastError is { Length: > 0 } error) Console.WriteLine($"複製エラー  : {error}");
        foreach (var problem in db.PrerequisiteProblems ?? []) Console.WriteLine($"前提条件    : {problem}");
    }
    if (h.Settings is { } s)
    {
        Console.WriteLine($"設定        : {(s.Enabled ? "有効" : "無効")}、{s.Interface} VIP {s.VirtualIp} VRID {s.VirtualRouterId} 優先度 {s.Priority}、復帰 {s.ReturnMode}");
        Console.WriteLine($"再参加先    : {s.ReplicationSourceHost}:{s.ReplicationSourcePort}（ユーザー {s.ReplicationUser ?? "未設定"}）、DB管理用接続 {s.LocalDbTarget ?? "未設定"}");
    }
    else
    {
        Console.WriteLine("設定        : 未設定（systemagent ha set）");
    }
    foreach (var e in h.History.Take(10)) Console.WriteLine($"  {Formatting.DateTime(e.At)} {e.Message}");
}));
haCommand.Subcommands.Add(haStatus);

var haInterface = new Option<string>("--interface") { Description = "VRRPのインターフェース（例: eth0）", Required = true };
var haVip = new Option<string>("--vip") { Description = "VIP（CIDR形式、例: 10.0.0.100/24）", Required = true };
var haVrid = new Option<int>("--vrid") { Description = "virtual_router_id（1〜255、クラスタ内で共通）", Required = true };
var haPriority = new Option<int>("--priority") { Description = "優先度（1〜254、大きいほどMASTERになりやすい）", Required = true };
var haAuth = new Option<bool>("--auth") { Description = "VRRPの認証パスワードを設定する（対話入力、英数字8文字以内）" };
var haPeers = new Option<string[]>("--peer") { Description = "ユニキャストの相手IP（複数指定可。省略時はマルチキャスト）", AllowMultipleArgumentsPerToken = true };
var haSource = new Option<string?>("--unicast-src") { Description = "ユニキャストの送信元IP" };
var haMode = new Option<SystemAgent.Core.Ha.HaReturnMode>("--return-mode")
{
    Description = "元マスター復帰時の動作（Auto: 自動でレプリカとして再参加 / Manual: 管理者の承認待ち）",
    DefaultValueFactory = _ => SystemAgent.Core.Ha.HaReturnMode.Manual,
};
var haDbHost = new Option<string>("--db-host") { Description = "このノードのMariaDB（昇格・降格に使う）", DefaultValueFactory = _ => "127.0.0.1" };
var haDbPort = new Option<int>("--db-port") { Description = "このノードのMariaDBのポート", DefaultValueFactory = _ => 3306 };
var haDbUser = new Option<string>("--db-user") { Description = "管理者ユーザー（SUPER / REPLICATION権限が必要）", DefaultValueFactory = _ => "root" };
var haReplUser = new Option<string?>("--repl-user") { Description = "レプリケーション用ユーザー" };
var haReplHost = new Option<string?>("--repl-host") { Description = "再参加先ホスト（省略時はVIP）" };
var haReplPort = new Option<int>("--repl-port") { Description = "再参加先ポート", DefaultValueFactory = _ => 3306 };
var haDisable = new Option<bool>("--disable") { Description = "HAを無効にする（keepalivedを停止）" };
var haNoApply = new Option<bool>("--no-apply") { Description = "保存のみ行い、keepalived.confを適用しない" };
var haSet = new Command("set", "HA設定を保存して keepalived.conf を適用する（パスワード類は対話入力。空Enterで既存の値を維持）");
foreach (var o in new Option[] { haInterface, haVip, haVrid, haPriority, haAuth, haPeers, haSource, haMode, haDbHost, haDbPort, haDbUser, haReplUser, haReplHost, haReplPort, haDisable, haNoApply })
{
    haSet.Options.Add(o);
}
haSet.SetAction((p, ct) => RunOnNode(p, async api =>
{
    var authPass = p.GetValue(haAuth) ? ConsoleUi.ReadSecret("VRRP認証パスワード（空で認証なし）: ") : null;
    var dbPassword = ConsoleUi.ReadSecret($"{p.GetValue(haDbUser)}@{p.GetValue(haDbHost)} のパスワード（空Enterで既存を維持）: ");
    var replPassword = p.GetValue(haReplUser) is null ? "" : ConsoleUi.ReadSecret("レプリケーション用パスワード（空Enterで既存を維持）: ");
    await api.SaveHaAsync(new SetHaSettingsRequest(
        !p.GetValue(haDisable), p.GetValue(haInterface)!, p.GetValue(haVip)!, p.GetValue(haVrid), p.GetValue(haPriority),
        authPass, p.GetValue(haPeers), p.GetValue(haSource), p.GetValue(haMode),
        p.GetValue(haDbHost), p.GetValue(haDbPort), p.GetValue(haDbUser), dbPassword,
        p.GetValue(haReplUser), replPassword, p.GetValue(haReplHost), p.GetValue(haReplPort), !p.GetValue(haNoApply)), ct);
    Console.WriteLine(p.GetValue(haNoApply) ? "HA設定を保存しました。" : "HA設定を保存し、keepalived に適用しました。");
}));
haCommand.Subcommands.Add(haSet);

var haApply = new Command("apply", "保存済みのHA設定で keepalived.conf を生成・適用する");
haApply.SetAction((p, ct) => RunOnNode(p, async api =>
{
    await api.ApplyHaAsync(ct);
    Console.WriteLine("keepalived に適用しました。");
}));
haCommand.Subcommands.Add(haApply);

var haRejoin = new Command("rejoin", "このノードをレプリカとして再参加させる（手動復帰モードの承認）");
haRejoin.SetAction((p, ct) => RunOnNode(p, async api =>
{
    await api.RejoinHaAsync(ct);
    Console.WriteLine("レプリカとして再参加しました。");
}));
haCommand.Subcommands.Add(haRejoin);

// keepalived の notify から root で呼ばれる。ログインセッションは使わず、root専用のフック用トークンで認証する
var notifyState = new Argument<SystemAgent.Core.Ha.VrrpState>("state") { Description = "MASTER / BACKUP / FAULT / STOP" };
var hookTokenFile = new Option<string>("--hook-token-file") { DefaultValueFactory = _ => "/var/lib/systemagent/ha-hook-token", Hidden = true };
var haNotify = new Command("notify", "keepalived の notify から呼ばれる（手動では通常使わない）");
haNotify.Arguments.Add(notifyState);
haNotify.Options.Add(hookTokenFile);
haNotify.SetAction(async (p, ct) =>
{
    try
    {
        var token = (await File.ReadAllTextAsync(p.GetValue(hookTokenFile)!, ct)).Trim();
        using var http = new HttpClient { BaseAddress = new Uri(ResolveUrl(p).TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(5) };
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/ha/notify")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new HaNotifyRequest(p.GetValue(notifyState)), options: ApiClient.Json),
        };
        request.Headers.Add("X-SystemAgent-Hook-Token", token);
        using var response = await http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return 0;
        Console.Error.WriteLine($"通知に失敗しました（{(int)response.StatusCode}）: {await response.Content.ReadAsStringAsync(ct)}");
        return 1;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
    {
        Console.Error.WriteLine($"通知に失敗しました: {ex.Message}");
        return 1;
    }
});
haCommand.Subcommands.Add(haNotify);
root.Subcommands.Add(haCommand);

// --- service ---
var service = new Command("service", "サービス（systemd）の状態確認と操作");
var serviceList = new Command("list", "管理対象サービスの状態を一覧表示する");
serviceList.SetAction((p, ct) => RunOnNode(p, async api =>
{
    var services = await api.GetServicesAsync(ct);
    if (p.GetValue(jsonOption)) { ConsoleUi.WriteJson(services); return; }
    ConsoleUi.WriteTable(["サービス", "状態", "自動起動", "開始時刻", "説明"],
        services.Select(s => (IReadOnlyList<string>)
        [
            s.Status.Name, $"{s.Status.ActiveState} ({s.Status.SubState})", s.Status.UnitFileState,
            Formatting.DateTime(s.Status.ActiveSince), s.Status.Description,
        ]));
}));
service.Subcommands.Add(serviceList);
var unitArgument = new Argument<string>("service") { Description = "サービス名（例: chronyd）" };
var serviceStatus = new Command("status", "サービスの状態を表示する");
serviceStatus.Arguments.Add(unitArgument);
serviceStatus.SetAction((p, ct) => RunOnNode(p, async api =>
{
    var s = await api.GetServiceAsync(p.GetValue(unitArgument)!, ct);
    if (p.GetValue(jsonOption)) { ConsoleUi.WriteJson(s); return; }
    Console.WriteLine($"{s.Status.Name}: {s.Status.Description}");
    Console.WriteLine($"  状態    : {s.Status.ActiveState} ({s.Status.SubState})、PID {s.Status.MainPid?.ToString() ?? "-"}、開始 {Formatting.DateTime(s.Status.ActiveSince)}");
    Console.WriteLine($"  自動起動: {s.Status.UnitFileState}");
    Console.WriteLine($"  操作    : {(s.Operable ? "可" : "不可（管理対象外）")}");
}));
service.Subcommands.Add(serviceStatus);
foreach (var action in Enum.GetValues<SystemAgent.Core.CapabilityProviders.ServiceAction>())
{
    var (description, done) = action switch
    {
        SystemAgent.Core.CapabilityProviders.ServiceAction.Start => ("サービスを起動する", "を起動しました"),
        SystemAgent.Core.CapabilityProviders.ServiceAction.Stop => ("サービスを停止する", "を停止しました"),
        SystemAgent.Core.CapabilityProviders.ServiceAction.Restart => ("サービスを再起動する", "を再起動しました"),
        SystemAgent.Core.CapabilityProviders.ServiceAction.Enable => ("サービスの自動起動を有効化する", "の自動起動を有効化しました"),
        _ => ("サービスの自動起動を無効化する", "の自動起動を無効化しました"),
    };
    var command = new Command(action.ToString().ToLowerInvariant(), description);
    command.Arguments.Add(unitArgument);
    command.SetAction((p, ct) => RunOnNode(p, async api =>
    {
        await api.ServiceActionAsync(p.GetValue(unitArgument)!, action, ct);
        Console.WriteLine($"{p.GetValue(unitArgument)} {done}。");
    }));
    service.Subcommands.Add(command);
}
var linesOption = new Option<int>("--lines", "-n") { Description = "表示する行数", DefaultValueFactory = _ => 100 };
var serviceLogs = new Command("logs", "サービスのログ（journal）を表示する");
serviceLogs.Arguments.Add(unitArgument);
serviceLogs.Options.Add(linesOption);
serviceLogs.SetAction((p, ct) => RunOnNode(p, async api =>
    Console.Write((await api.GetServiceLogsAsync(p.GetValue(unitArgument)!, p.GetValue(linesOption), ct)).Logs)));
service.Subcommands.Add(serviceLogs);
root.Subcommands.Add(service);

// --- registry ---
var registry = new Command("registry", "コンテナレジストリの認証情報（pull時に自動でログインする）");
var registryList = new Command("list", "登録済みのレジストリを一覧表示する");
registryList.SetAction((p, ct) => RunOnNode(p, async api =>
{
    var registries = await api.GetRegistriesAsync(ct);
    if (p.GetValue(jsonOption)) { ConsoleUi.WriteJson(registries); return; }
    if (registries.Count == 0) { Console.WriteLine("登録されたレジストリはありません。"); return; }
    ConsoleUi.WriteTable(["レジストリ", "ユーザー", "更新日時"],
        registries.Select(r => (IReadOnlyList<string>)[r.Registry, r.Username, Formatting.DateTime(r.UpdatedAt)]));
}));
registry.Subcommands.Add(registryList);
var registryArgument = new Argument<string>("registry") { Description = "レジストリ（ホスト名[:ポート]。Docker Hubは docker.io）" };
var registryUser = new Option<string?>("--username", "-u") { Description = "ユーザー名（省略時は登録済みの値で再ログイン）" };
var registryPasswordStdin = new Option<bool>("--password-stdin") { Description = "パスワード（アクセストークン）を標準入力から読む" };
var registryLogin = new Command("login", "レジストリにログインし、成功したら認証情報を保存する");
registryLogin.Arguments.Add(registryArgument);
registryLogin.Options.Add(registryUser);
registryLogin.Options.Add(registryPasswordStdin);
registryLogin.SetAction((p, ct) => RunOnNode(p, async api =>
{
    var name = p.GetValue(registryArgument)!;
    var userName = p.GetValue(registryUser);
    string? password = null;
    if (userName is null)
    {
        // 登録済みの認証情報で再ログイン（ログイン確認）
        userName = (await api.GetRegistriesAsync(ct)).FirstOrDefault(r => r.Registry == name.Trim().ToLowerInvariant())?.Username
            ?? throw new CliException($"{name} は登録されていません。--username を指定してください。");
    }
    else
    {
        password = p.GetValue(registryPasswordStdin)
            ? Console.In.ReadToEnd().TrimEnd('\r', '\n')
            : ConsoleUi.ReadSecret("パスワード（空Enterで登録済みの値を使用）: ");
    }
    await api.SaveRegistryAsync(new SaveRegistryRequest(name, userName, string.IsNullOrEmpty(password) ? null : password), ct);
    Console.WriteLine($"{userName}@{name} でログインし、保存しました。");
}));
registry.Subcommands.Add(registryLogin);
var registryLogout = new Command("logout", "レジストリからログアウトし、認証情報を削除する");
registryLogout.Arguments.Add(registryArgument);
registryLogout.SetAction((p, ct) => RunOnNode(p, async api =>
{
    await api.RemoveRegistryAsync(p.GetValue(registryArgument)!, ct);
    Console.WriteLine($"{p.GetValue(registryArgument)} の認証情報を削除しました。");
}));
registry.Subcommands.Add(registryLogout);
root.Subcommands.Add(registry);

// --- deploy ---
var deploy = new Command("deploy", "アプリ（コンテナ）の定義とイメージ更新・デプロイ（このノード）");
var deployList = new Command("list", "アプリを一覧表示する");
deployList.SetAction((p, ct) => RunOnNode(p, async api =>
{
    var apps = await api.GetDeploymentsAsync(ct);
    if (p.GetValue(jsonOption)) { ConsoleUi.WriteJson(apps); return; }
    if (apps.Count == 0) { Console.WriteLine("アプリは定義されていません（deploy define で追加）。"); return; }
    ConsoleUi.WriteTable(["アプリ", "イメージ", "現在", "1つ前", "状態", "デプロイ日時"],
        apps.Select(a => (IReadOnlyList<string>)
        [
            a.Spec.Name, a.Spec.Image, a.CurrentTag ?? "-", a.PreviousTag ?? "-",
            a.Deploying ? "デプロイ中" : a.State?.ToString() ?? "コンテナなし", Formatting.DateTime(a.LastDeployedAt),
        ]));
}));
deploy.Subcommands.Add(deployList);
var appArgument = new Argument<string>("app") { Description = "アプリ（コンテナ名）" };
var deployShow = new Command("show", "アプリの定義と履歴を表示する");
deployShow.Arguments.Add(appArgument);
deployShow.SetAction((p, ct) => RunOnNode(p, async api =>
{
    var a = await api.GetDeploymentAsync(p.GetValue(appArgument)!, ct);
    if (p.GetValue(jsonOption)) { ConsoleUi.WriteJson(a); return; }
    var s = a.Spec;
    Console.WriteLine($"{s.Name}: {s.Image}:{a.CurrentTag ?? "（未デプロイ）"}  状態 {(a.Deploying ? "デプロイ中" : a.State?.ToString() ?? "コンテナなし")}");
    Console.WriteLine($"  1つ前   : {a.PreviousTag ?? "-"}");
    Console.WriteLine($"  取得方針: {s.Pull}、再起動 {s.Restart}、動作確認 {s.HealthCheckSeconds}秒{(s.Pod is null ? "" : $"、Pod {s.Pod}")}");
    foreach (var (label, values) in new[] { ("ポート", s.Ports), ("環境変数", s.Environment), ("ボリューム", s.Volumes) })
        Console.WriteLine($"  {label,-6}: {(values.Count == 0 ? "-" : string.Join(", ", values))}");
    if (a.History.Count > 0)
    {
        Console.WriteLine("履歴:");
        foreach (var e in a.History)
            Console.WriteLine($"  {Formatting.DateTime(e.At)} {e.User} {e.Action} {e.FromTag ?? "-"} → {e.ToTag} {(e.Success ? "成功" : "失敗")}: {e.Message.Split('\n')[0]}");
    }
}));
deploy.Subcommands.Add(deployShow);

var deployImage = new Option<string>("--image") { Description = "タグを除いたイメージ（例: registry.example.com/app/web）", Required = true };
var deployPorts = new Option<string[]>("--port", "-p") { Description = "ポート公開（例: 8080:80）。複数指定可" };
var deployEnv = new Option<string[]>("--env", "-e") { Description = "環境変数 KEY=VALUE。複数指定可" };
var deployVolumes = new Option<string[]>("--volume", "-v") { Description = "ボリューム（/host:/container[:ro]）。複数指定可" };
var deployPod = new Option<string?>("--pod") { Description = "参加するPod（Podmanのみ）" };
var deployRestart = new Option<string>("--restart") { Description = "再起動ポリシー（no / always / unless-stopped / on-failure）", DefaultValueFactory = _ => "always" };
var deployPull = new Option<SystemAgent.Core.Deploy.PullPolicy>("--pull") { Description = "Missing: ローカルに無い時だけ取得（既定） / Always: 毎回取得", DefaultValueFactory = _ => SystemAgent.Core.Deploy.PullPolicy.Missing };
var deployHealth = new Option<int>("--health-seconds") { Description = "起動後に動作中か確認するまでの秒数", DefaultValueFactory = _ => 10 };
var deployDefine = new Command("define", "アプリを定義する（既存なら置き換え。デプロイはしない）");
deployDefine.Arguments.Add(appArgument);
foreach (var option in new Option[] { deployImage, deployPorts, deployEnv, deployVolumes, deployPod, deployRestart, deployPull, deployHealth })
    deployDefine.Options.Add(option);
deployDefine.SetAction((p, ct) => RunOnNode(p, async api =>
{
    await api.SaveDeploymentAsync(new SystemAgent.Core.Deploy.DeploymentSpec(
        p.GetValue(appArgument)!, p.GetValue(deployImage)!,
        p.GetValue(deployPorts) ?? [], p.GetValue(deployEnv) ?? [], p.GetValue(deployVolumes) ?? [],
        p.GetValue(deployPod), p.GetValue(deployRestart)!, p.GetValue(deployPull), p.GetValue(deployHealth)), ct);
    Console.WriteLine($"{p.GetValue(appArgument)} を定義しました。デプロイは deploy run {p.GetValue(appArgument)} <タグ>。");
}));
deploy.Subcommands.Add(deployDefine);

var tagArgument = new Argument<string>("tag") { Description = "デプロイするイメージのタグ（例: 1.2.3）" };
var deployRun = new Command("run", "指定したタグをデプロイする（失敗したら元のコンテナに戻す）");
deployRun.Arguments.Add(appArgument);
deployRun.Arguments.Add(tagArgument);
deployRun.SetAction((p, ct) => RunOnNode(p, async api =>
{
    Console.WriteLine($"{p.GetValue(appArgument)} に {p.GetValue(tagArgument)} をデプロイしています...");
    var a = await api.DeployAsync(p.GetValue(appArgument)!, p.GetValue(tagArgument)!, ct);
    Console.WriteLine($"デプロイしました: {a.Spec.Image}:{a.CurrentTag}（状態 {a.State}）");
}));
deploy.Subcommands.Add(deployRun);
var deployRollback = new Command("rollback", "1つ前のタグに戻す");
deployRollback.Arguments.Add(appArgument);
deployRollback.SetAction((p, ct) => RunOnNode(p, async api =>
{
    var a = await api.RollbackAsync(p.GetValue(appArgument)!, ct);
    Console.WriteLine($"{a.Spec.Image}:{a.CurrentTag} に戻しました（状態 {a.State}）。");
}));
deploy.Subcommands.Add(deployRollback);
var withContainer = new Option<bool>("--with-container") { Description = "コンテナも停止・削除する" };
var deployRemove = new Command("remove", "アプリの定義を削除する");
deployRemove.Arguments.Add(appArgument);
deployRemove.Options.Add(withContainer);
deployRemove.SetAction((p, ct) => RunOnNode(p, async api =>
{
    await api.RemoveDeploymentAsync(p.GetValue(appArgument)!, p.GetValue(withContainer), ct);
    Console.WriteLine($"{p.GetValue(appArgument)} の定義を削除しました{(p.GetValue(withContainer) ? "（コンテナも削除）" : "")}。");
}));
deploy.Subcommands.Add(deployRemove);
root.Subcommands.Add(deploy);

// --- audit ---
var audit = new Command("audit", "監査ログ（全ノードの操作記録。中央DB）");
var auditSince = new Option<DateOnly?>("--since") { Description = "開始日（例: 2026-09-01）" };
var auditUntil = new Option<DateOnly?>("--until") { Description = "終了日（この日を含む）" };
var auditUser = new Option<string?>("--user") { Description = "実行者" };
var auditAction = new Option<string?>("--action") { Description = "操作（前方一致。例: deploy）" };
var auditNode = new Option<string?>("--node-name") { Description = "操作を実行したノード" };
var auditGrep = new Option<string?>("--grep") { Description = "内容に含む文字" };
Option[] auditFilters = [auditSince, auditUntil, auditUser, auditAction, auditNode, auditGrep];
AuditLogQuery AuditQuery(ParseResult p, int page, int pageSize)
{
    static DateTimeOffset? Local(DateOnly? date) =>
        date?.ToDateTime(TimeOnly.MinValue) is { } d ? new DateTimeOffset(d, TimeZoneInfo.Local.GetUtcOffset(d)) : null;
    return new AuditLogQuery(Local(p.GetValue(auditSince)), Local(p.GetValue(auditUntil)?.AddDays(1)),
        p.GetValue(auditUser), p.GetValue(auditAction), p.GetValue(auditNode), p.GetValue(auditGrep), page, pageSize);
}
var auditLimit = new Option<int>("--limit", "-n") { Description = "表示件数（最大500）", DefaultValueFactory = _ => 50 };
var auditPage = new Option<int>("--page") { Description = "ページ（1が最新）", DefaultValueFactory = _ => 1 };
var auditList = new Command("list", "監査ログを新しい順に表示する");
foreach (var option in auditFilters.Append(auditLimit).Append(auditPage)) auditList.Options.Add(option);
auditList.SetAction((p, ct) => RunOnNode(p, async api =>
{
    var result = await api.GetAuditLogsAsync(AuditQuery(p, p.GetValue(auditPage), p.GetValue(auditLimit)), ct);
    if (p.GetValue(jsonOption)) { ConsoleUi.WriteJson(result); return; }
    if (result.TotalCount == 0) { Console.WriteLine("条件に合う記録はありません。"); return; }
    ConsoleUi.WriteTable(["日時", "実行者", "操作", "ノード", "内容"],
        result.Items.Select(e => (IReadOnlyList<string>)
        [
            e.OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), e.Actor, e.Action, e.NodeName ?? "-",
            (e.Detail ?? "").ReplaceLineEndings(" "),
        ]));
    var first = (result.Page - 1) * result.PageSize + 1;
    Console.WriteLine($"{result.TotalCount} 件中 {first}〜{first + result.Items.Count - 1} 件目");
}));
audit.Subcommands.Add(auditList);
var auditOut = new Option<FileInfo>("--output", "-o") { Description = "保存先のCSVファイル", Required = true };
var auditExport = new Command("export", "条件に合う監査ログをCSVで保存する（最大10万件）");
foreach (var option in auditFilters.Append(auditOut)) auditExport.Options.Add(option);
auditExport.SetAction((p, ct) => RunOnNode(p, async api =>
{
    var file = p.GetValue(auditOut)!;
    await using (var source = await api.ExportAuditCsvAsync(AuditQuery(p, 1, 1), ct))
    await using (var target = file.Create())
    {
        await source.CopyToAsync(target, ct);
    }
    Console.WriteLine($"{file.FullName} に保存しました。");
}));
audit.Subcommands.Add(auditExport);
root.Subcommands.Add(audit);

// --- backup ---
var backup = new Command("backup", "中央DBのバックアップ（保存先はこのノード）");
var backupList = new Command("list", "バックアップと設定を一覧表示する");
backupList.SetAction((p, ct) => RunOnNode(p, async api =>
{
    var b = await api.GetBackupsAsync(ct);
    if (p.GetValue(jsonOption)) { ConsoleUi.WriteJson(b); return; }
    var s = b.Settings;
    Console.WriteLine($"保存先: {s.Directory}（保持 {s.Retention} 世代、帯域 {(s.RateLimitKBps == 0 ? "無制限" : $"{s.RateLimitKBps} KB/秒")}、定時 {s.DailyAt ?? "なし"}、ツール {s.Tool ?? "なし"}）");
    if (b.Files.Count == 0) { Console.WriteLine("バックアップはありません。"); return; }
    ConsoleUi.WriteTable(["ファイル", "サイズ", "作成日時"],
        b.Files.Select(f => (IReadOnlyList<string>)[f.Name, Formatting.Bytes(f.SizeBytes), Formatting.DateTime(f.CreatedAt)]));
}));
backup.Subcommands.Add(backupList);
var backupCreate = new Command("create", "今すぐバックアップを作成する");
backupCreate.SetAction((p, ct) => RunOnNode(p, async api =>
{
    Console.Error.WriteLine("バックアップを作成しています...");
    var f = await api.CreateBackupAsync(ct);
    Console.WriteLine($"作成しました: {f.Name}（{Formatting.Bytes(f.SizeBytes)}）");
}));
backup.Subcommands.Add(backupCreate);
var backupNameArgument = new Argument<string>("name") { Description = "バックアップのファイル名（backup list で確認）" };
var outputOption = new Option<FileInfo?>("--output", "-o") { Description = "保存先（省略時はカレントディレクトリに同名で保存）" };
var backupDownload = new Command("download", "バックアップをダウンロードする");
backupDownload.Arguments.Add(backupNameArgument);
backupDownload.Options.Add(outputOption);
backupDownload.SetAction((p, ct) => RunOnNode(p, async api =>
{
    var name = p.GetValue(backupNameArgument)!;
    var output = p.GetValue(outputOption)?.FullName ?? Path.GetFullPath(name);
    await using (var source = await api.DownloadBackupAsync(name, ct))
    await using (var target = File.Create(output))
    {
        await source.CopyToAsync(target, ct);
    }
    Console.WriteLine($"保存しました: {output}");
}));
backup.Subcommands.Add(backupDownload);
var backupRemove = new Command("rm", "バックアップを削除する");
backupRemove.Arguments.Add(backupNameArgument);
backupRemove.SetAction((p, ct) => RunOnNode(p, async api =>
{
    await api.DeleteBackupAsync(p.GetValue(backupNameArgument)!, ct);
    Console.WriteLine("削除しました。");
}));
backup.Subcommands.Add(backupRemove);
var yesOption = new Option<bool>("--yes") { Description = "確認を省略する" };
var backupRestore = new Command("restore", "バックアップでDBを置き換える（現在のDBの内容は失われる）");
backupRestore.Arguments.Add(backupNameArgument);
backupRestore.Options.Add(yesOption);
backupRestore.SetAction((p, ct) => RunOnNode(p, async api =>
{
    var name = p.GetValue(backupNameArgument)!;
    if (!p.GetValue(yesOption) && ConsoleUi.ReadLine($"DBを {name} の内容で置き換えます。続けるにはファイル名を入力してください: ") != name)
        throw new CliException("中止しました。");
    Console.Error.WriteLine("復元しています...");
    await api.RestoreBackupAsync(name, ct);
    Console.WriteLine("復元しました。必要に応じて systemagent db migrate を実行してください。");
}));
backup.Subcommands.Add(backupRestore);
root.Subcommands.Add(backup);

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
    ?? LocalUrlFromConfig()
    ?? DefaultUrl;

// このノードの /etc/systemagent/systemagent.json の Urls から、ローカルの接続先を決める（ポートを変更している場合のため）
static string? LocalUrlFromConfig()
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
