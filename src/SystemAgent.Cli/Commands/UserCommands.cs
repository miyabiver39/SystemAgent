using System.CommandLine;
using System.Net;
using SystemAgent.Client;
using SystemAgent.Core.Contracts;

namespace SystemAgent.Cli.Commands;

/// <summary>ユーザー管理（user）</summary>
internal static class UserCommands
{
    public static void Register(RootCommand root, CliContext cli)
    {
        var jsonOption = cli.JsonOption;
        Task<int> Run(ParseResult p, Func<ApiClient, Task> action) => cli.Run(p, action);

        var user = new Command("user", "ユーザー管理（通常認証のDBユーザー）");
        var userList = new Command("list", "ユーザーを一覧表示する");
        userList.SetAction((p, ct) => Run(p, async api =>
        {
            var users = await api.GetUsersAsync(ct);
            if (p.GetValue(jsonOption)) { ConsoleUi.WriteJson(users); return; }
            if (users.Count == 0) { Console.WriteLine("ユーザーはまだ登録されていません。"); return; }
            ConsoleUi.WriteTable(["ユーザー名", "作成日時"],
                users.Select(u => (IReadOnlyList<string>)[u.UserName, u.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")]));
        }));
        var userNameArgument = new Argument<string>("name") { Description = "ユーザー名" };
        var userCreate = new Command("create", "ユーザーを作成する（パスワードは対話入力）");
        userCreate.Arguments.Add(userNameArgument);
        userCreate.SetAction((p, ct) => Run(p, async api =>
        {
            var password = ConsoleUi.ReadNewPassword(PasswordPolicy.MinLength);
            try
            {
                await api.CreateUserAsync(new CreateUserRequest(p.GetValue(userNameArgument)!, password), ct);
            }
            catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
            {
                throw new CliException("同じ名前のユーザーが既に存在します。");
            }
            Console.WriteLine($"ユーザー {p.GetValue(userNameArgument)} を作成しました。");
        }));
        var userDelete = new Command("delete", "ユーザーを削除する");
        userDelete.Arguments.Add(userNameArgument);
        userDelete.SetAction((p, ct) => Run(p, async api =>
        {
            try
            {
                await api.DeleteUserAsync(p.GetValue(userNameArgument)!, ct);
            }
            catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                throw new CliException("指定されたユーザーは存在しません。");
            }
            Console.WriteLine($"ユーザー {p.GetValue(userNameArgument)} を削除しました。");
        }));
        user.Subcommands.Add(userList);
        user.Subcommands.Add(userCreate);
        user.Subcommands.Add(userDelete);
        root.Subcommands.Add(user);
    }
}
