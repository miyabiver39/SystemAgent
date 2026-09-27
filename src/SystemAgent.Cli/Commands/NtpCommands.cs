using System.CommandLine;
using SystemAgent.Client;

namespace SystemAgent.Cli.Commands;

/// <summary>時刻同期（ntp）</summary>
internal static class NtpCommands
{
    public static void Register(RootCommand root, CliContext cli)
    {
        var jsonOption = cli.JsonOption;
        Task<int> RunOnNode(ParseResult p, Func<ApiClient, Task> action) => cli.RunOnNode(p, action);

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
    }
}
