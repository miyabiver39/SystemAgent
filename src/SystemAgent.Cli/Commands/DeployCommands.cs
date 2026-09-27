using System.CommandLine;
using SystemAgent.Client;

namespace SystemAgent.Cli.Commands;

/// <summary>アプリのデプロイ（deploy）</summary>
internal static class DeployCommands
{
    public static void Register(RootCommand root, CliContext cli)
    {
        var jsonOption = cli.JsonOption;
        Task<int> RunOnNode(ParseResult p, Func<ApiClient, Task> action) => cli.RunOnNode(p, action);

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
    }
}
