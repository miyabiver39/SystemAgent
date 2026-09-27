using System.CommandLine;
using SystemAgent.Client;

namespace SystemAgent.Cli.Commands;

/// <summary>DBバックアップ（backup）</summary>
internal static class BackupCommands
{
    public static void Register(RootCommand root, CliContext cli)
    {
        var jsonOption = cli.JsonOption;
        Task<int> RunOnNode(ParseResult p, Func<ApiClient, Task> action) => cli.RunOnNode(p, action);

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
    }
}
