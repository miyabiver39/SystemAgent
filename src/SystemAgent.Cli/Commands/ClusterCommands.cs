using System.CommandLine;
using System.Net;
using SystemAgent.Client;

namespace SystemAgent.Cli.Commands;

/// <summary>ノード・クラスタ（node / cluster / join）</summary>
internal static class ClusterCommands
{
    public static void Register(RootCommand root, CliContext cli)
    {
        var jsonOption = cli.JsonOption;
        Task<int> Run(ParseResult p, Func<ApiClient, Task> action) => cli.Run(p, action);

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
    }
}
