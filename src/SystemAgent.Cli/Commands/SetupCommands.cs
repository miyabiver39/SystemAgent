using System.CommandLine;
using System.Net;
using SystemAgent.Client;
using SystemAgent.Core.Contracts;
using static SystemAgent.Cli.CliContext;

namespace SystemAgent.Cli.Commands;

/// <summary>初期セットアップ（setup）</summary>
internal static class SetupCommands
{
    public static void Register(RootCommand root, CliContext cli)
    {
        Task<int> Run(ParseResult p, Func<ApiClient, Task> action) => cli.Run(p, action);

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
    }
}
