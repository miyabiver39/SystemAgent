using System.CommandLine;
using SystemAgent.Client;

namespace SystemAgent.Cli.Commands;

/// <summary>コンテナ・Pod・イメージ（container / pod / image）</summary>
internal static class ContainerCommands
{
    public static void Register(RootCommand root, CliContext cli)
    {
        var jsonOption = cli.JsonOption;
        Task<int> RunOnNode(ParseResult p, Func<ApiClient, Task> action) => cli.RunOnNode(p, action);

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
        var pushTargetArgument = new Argument<string>("target") { Description = "送り先（レジストリ/リポジトリ:タグ。例: zot.example.com:5000/app/web:1.0）" };
        var imagePush = new Command("push", "ローカルのイメージを登録済みのレジストリへ送る");
        imagePush.Arguments.Add(imageArgument);
        imagePush.Arguments.Add(pushTargetArgument);
        imagePush.SetAction((p, ct) => RunOnNode(p, async api =>
        {
            Console.Error.WriteLine($"{p.GetValue(imageArgument)} を {p.GetValue(pushTargetArgument)} へ送っています...");
            await api.PushImageAsync(p.GetValue(imageArgument)!, p.GetValue(pushTargetArgument)!, ct);
            Console.WriteLine("送りました。");
        }));
        image.Subcommands.Add(imagePush);
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
    }
}
