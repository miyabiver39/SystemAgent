using System.CommandLine;
using System.Net;
using SystemAgent.Client;
using SystemAgent.Core.Contracts;
using static SystemAgent.Cli.CliContext;

namespace SystemAgent.Cli.Commands;

/// <summary>ログイン・ログアウト・状態表示・パスワード変更</summary>
internal static class AuthCommands
{
    public static void Register(RootCommand root, CliContext cli)
    {
        var jsonOption = cli.JsonOption;
        var tokens = cli.Tokens;
        Task<int> Run(ParseResult p, Func<ApiClient, Task> action) => cli.Run(p, action);
        string ResolveUrl(ParseResult p) => cli.ResolveUrl(p);

        var loginUserOption = new Option<string?>("--user", "-u") { Description = "ユーザー名" };
        var emergencyOption = new Option<bool>("--emergency") { Description = "緊急ログイン（ローカル認証。DB停止中も利用可能）" };
        var login = new Command("login", "ログインする");
        login.Options.Add(loginUserOption);
        login.Options.Add(emergencyOption);
        login.SetAction((p, ct) => Run(p, async api =>
        {
            var userName = p.GetValue(loginUserOption) ?? ConsoleUi.ReadLine("ユーザー名: ");
            var password = ConsoleUi.ReadSecret("パスワード: ");
            var emergency = p.GetValue(emergencyOption);
            TokenResponse token;
            try
            {
                token = await api.LoginAsync(userName, password, emergency, ct);
            }
            catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new CliException("ユーザー名またはパスワードが正しくありません。");
            }
            catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.ServiceUnavailable && !emergency)
            {
                throw new CliException("データベースに接続できません。--emergency を付けて緊急ログインしてください。");
            }
            tokens.Save(new Session(ResolveUrl(p), userName, token));
            Console.WriteLine($"{userName} としてログインしました（{AuthSourceLabel(token.AuthSource)}、有効期限 {token.ExpiresAt.ToLocalTime():yyyy-MM-dd HH:mm}）。");
        }));
        root.Subcommands.Add(login);

        var logout = new Command("logout", "ログアウトする（保存したトークンを削除）");
        logout.SetAction(_ =>
        {
            tokens.Clear();
            Console.WriteLine("ログアウトしました。");
        });
        root.Subcommands.Add(logout);

        var status = new Command("status", "接続先・DB接続状態・ログイン状態を表示する");
        status.SetAction((p, ct) => Run(p, async api =>
        {
            var health = await api.GetHealthAsync(ct);
            var setupRequired = (await api.GetSetupStatusAsync(ct)).Required;
            MeResponse? me = tokens.Load() is null ? null : await api.GetMeAsync(ct);
            if (p.GetValue(jsonOption))
            {
                ConsoleUi.WriteJson(new { url = ResolveUrl(p), health, setupRequired, me });
                return;
            }
            Console.WriteLine($"接続先      : {ResolveUrl(p)}");
            if (setupRequired) Console.WriteLine("初期設定    : 未実施（`sudo systemagent setup` を実行してください）");
            Console.WriteLine($"データベース: {(health.Database ? "接続中" : "接続不可（緊急ログインのみ利用可能）")}");
            Console.WriteLine($"ログイン    : {(me is null ? "未ログイン" : $"{me.UserName}（{AuthSourceLabel(me.AuthSource)}）")}");
        }));
        root.Subcommands.Add(status);

        var passwd = new Command("passwd", "ログイン中のユーザーのパスワードを変更する");
        passwd.SetAction((p, ct) => Run(p, async api =>
        {
            var me = await api.GetMeAsync(ct);
            var current = ConsoleUi.ReadSecret("現在のパスワード: ");
            var password = ConsoleUi.ReadNewPassword(PasswordPolicy.MinLength);
            await api.ChangePasswordAsync(me.UserName, current, password, emergency: me.AuthSource == AuthSources.Emergency, ct);
            Console.WriteLine("パスワードを変更しました。");
        }));
        root.Subcommands.Add(passwd);
    }
}
