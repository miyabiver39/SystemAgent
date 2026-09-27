using System.CommandLine;
using SystemAgent.Client;

namespace SystemAgent.Cli.Commands;

/// <summary>環境情報（env）</summary>
internal static class EnvironmentCommands
{
    public static void Register(RootCommand root, CliContext cli)
    {
        var jsonOption = cli.JsonOption;
        Task<int> RunOnNode(ParseResult p, Func<ApiClient, Task> action) => cli.RunOnNode(p, action);

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
    }
}
