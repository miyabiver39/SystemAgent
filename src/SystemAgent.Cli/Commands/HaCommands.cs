using System.CommandLine;
using SystemAgent.Client;
using SystemAgent.Core.Contracts;

namespace SystemAgent.Cli.Commands;

/// <summary>冗長化（ha）</summary>
internal static class HaCommands
{
    public static void Register(RootCommand root, CliContext cli)
    {
        var jsonOption = cli.JsonOption;
        Task<int> RunOnNode(ParseResult p, Func<ApiClient, Task> action) => cli.RunOnNode(p, action);
        string ResolveUrl(ParseResult p) => cli.ResolveUrl(p);

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
    }
}
