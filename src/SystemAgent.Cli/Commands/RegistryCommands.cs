using System.CommandLine;
using SystemAgent.Client;
using SystemAgent.Core.Contracts;

namespace SystemAgent.Cli.Commands;

/// <summary>コンテナレジストリ（registry）</summary>
internal static class RegistryCommands
{
    public static void Register(RootCommand root, CliContext cli)
    {
        var jsonOption = cli.JsonOption;
        Task<int> RunOnNode(ParseResult p, Func<ApiClient, Task> action) => cli.RunOnNode(p, action);

        var registry = new Command("registry", "コンテナレジストリの認証情報（pull時に自動でログインする）");
        var registryList = new Command("list", "登録済みのレジストリを一覧表示する");
        registryList.SetAction((p, ct) => RunOnNode(p, async api =>
        {
            var registries = await api.GetRegistriesAsync(ct);
            if (p.GetValue(jsonOption)) { ConsoleUi.WriteJson(registries); return; }
            if (registries.Count == 0) { Console.WriteLine("登録されたレジストリはありません。"); return; }
            ConsoleUi.WriteTable(["レジストリ", "ユーザー", "証明書の検証", "更新日時"],
                registries.Select(r => (IReadOnlyList<string>)[r.Registry, r.Username, r.TlsVerify ? "する" : "しない", Formatting.DateTime(r.UpdatedAt)]));
        }));
        registry.Subcommands.Add(registryList);
        var registryArgument = new Argument<string>("registry") { Description = "レジストリ（ホスト名[:ポート]。Docker Hubは docker.io）" };
        var registryUser = new Option<string?>("--username", "-u") { Description = "ユーザー名（省略時は登録済みの値で再ログイン）" };
        var registryPasswordStdin = new Option<bool>("--password-stdin") { Description = "パスワード（アクセストークン）を標準入力から読む" };
        var registryNoTlsVerify = new Option<bool>("--no-tls-verify") { Description = "証明書を検証しない（自己署名証明書・HTTPのレジストリ。Podmanのみ）" };
        var registryLogin = new Command("login", "レジストリにログインし、成功したら認証情報を保存する");
        registryLogin.Arguments.Add(registryArgument);
        registryLogin.Options.Add(registryUser);
        registryLogin.Options.Add(registryPasswordStdin);
        registryLogin.Options.Add(registryNoTlsVerify);
        registryLogin.SetAction((p, ct) => RunOnNode(p, async api =>
        {
            var name = p.GetValue(registryArgument)!;
            var userName = p.GetValue(registryUser);
            var tlsVerify = !p.GetValue(registryNoTlsVerify);
            string? password = null;
            if (userName is null)
            {
                // 登録済みの認証情報で再ログイン（ログイン確認）
                var registered = (await api.GetRegistriesAsync(ct)).FirstOrDefault(r => r.Registry == name.Trim().ToLowerInvariant())
                    ?? throw new CliException($"{name} は登録されていません。--username を指定してください。");
                userName = registered.Username;
                tlsVerify = registered.TlsVerify && tlsVerify;
            }
            else
            {
                password = p.GetValue(registryPasswordStdin)
                    ? Console.In.ReadToEnd().TrimEnd('\r', '\n')
                    : ConsoleUi.ReadSecret("パスワード（空Enterで登録済みの値を使用）: ");
            }
            await api.SaveRegistryAsync(new SaveRegistryRequest(name, userName, string.IsNullOrEmpty(password) ? null : password, tlsVerify), ct);
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
        var registryRepos = new Command("repos", "レジストリ内のリポジトリを一覧表示する");
        registryRepos.Arguments.Add(registryArgument);
        registryRepos.SetAction((p, ct) => RunOnNode(p, async api =>
        {
            var repositories = await api.GetRegistryRepositoriesAsync(p.GetValue(registryArgument)!, ct);
            if (p.GetValue(jsonOption)) { ConsoleUi.WriteJson(repositories); return; }
            if (repositories.Count == 0) { Console.WriteLine("リポジトリはありません。"); return; }
            foreach (var repository in repositories) Console.WriteLine(repository);
        }));
        registry.Subcommands.Add(registryRepos);
        var repositoryArgument = new Argument<string>("repository") { Description = "リポジトリ（例: app/web）" };
        var registryTags = new Command("tags", "リポジトリのタグを一覧表示する");
        registryTags.Arguments.Add(registryArgument);
        registryTags.Arguments.Add(repositoryArgument);
        registryTags.SetAction((p, ct) => RunOnNode(p, async api =>
        {
            var result = await api.GetRegistryTagsAsync(p.GetValue(registryArgument)!, p.GetValue(repositoryArgument)!, ct);
            if (p.GetValue(jsonOption)) { ConsoleUi.WriteJson(result); return; }
            if (result.Tags.Count == 0) { Console.WriteLine("タグはありません。"); return; }
            foreach (var tag in result.Tags) Console.WriteLine($"{result.Repository}:{tag}");
        }));
        registry.Subcommands.Add(registryTags);
        var registryTagArgument = new Argument<string>("repository:tag") { Description = "削除するタグ（例: app/web:1.0）" };
        var registryDeleteTag = new Command("delete-tag", "レジストリからタグを削除する（同じ内容を指す他のタグは残る）");
        registryDeleteTag.Arguments.Add(registryArgument);
        registryDeleteTag.Arguments.Add(registryTagArgument);
        registryDeleteTag.SetAction((p, ct) => RunOnNode(p, async api =>
        {
            var value = p.GetValue(registryTagArgument)!;
            var colon = value.LastIndexOf(':');
            if (colon <= 0 || value.IndexOf('/', colon) >= 0) throw new CliException("リポジトリ:タグ の形式で指定してください（例: app/web:1.0）。");
            await api.DeleteRegistryTagAsync(p.GetValue(registryArgument)!, value[..colon], value[(colon + 1)..], ct);
            Console.WriteLine($"{p.GetValue(registryArgument)}/{value} を削除しました。");
        }));
        registry.Subcommands.Add(registryDeleteTag);
        root.Subcommands.Add(registry);
    }
}
