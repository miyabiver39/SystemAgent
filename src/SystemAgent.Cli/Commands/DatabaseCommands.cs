using System.CommandLine;
using SystemAgent.Client;
using SystemAgent.Core.Contracts;

namespace SystemAgent.Cli.Commands;

/// <summary>このノードのDB接続設定（db）</summary>
internal static class DatabaseCommands
{
    public static void Register(RootCommand root, CliContext cli)
    {
        var jsonOption = cli.JsonOption;
        Task<int> RunOnNode(ParseResult p, Func<ApiClient, Task> action) => cli.RunOnNode(p, action);

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
    }
}
