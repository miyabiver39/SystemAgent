using System.CommandLine;
using SystemAgent.Client;

namespace SystemAgent.Cli.Commands;

/// <summary>サービス管理（service）</summary>
internal static class ServiceCommands
{
    public static void Register(RootCommand root, CliContext cli)
    {
        var jsonOption = cli.JsonOption;
        Task<int> RunOnNode(ParseResult p, Func<ApiClient, Task> action) => cli.RunOnNode(p, action);

        var service = new Command("service", "サービス（systemd）の状態確認と操作");
        var serviceList = new Command("list", "管理対象サービスの状態を一覧表示する");
        serviceList.SetAction((p, ct) => RunOnNode(p, async api =>
        {
            var services = await api.GetServicesAsync(ct);
            if (p.GetValue(jsonOption)) { ConsoleUi.WriteJson(services); return; }
            ConsoleUi.WriteTable(["サービス", "状態", "自動起動", "開始時刻", "説明"],
                services.Select(s => (IReadOnlyList<string>)
                [
                    s.Status.Name, $"{s.Status.ActiveState} ({s.Status.SubState})", s.Status.UnitFileState,
                    Formatting.DateTime(s.Status.ActiveSince), s.Status.Description,
                ]));
        }));
        service.Subcommands.Add(serviceList);
        var unitArgument = new Argument<string>("service") { Description = "サービス名（例: chronyd）" };
        var serviceStatus = new Command("status", "サービスの状態を表示する");
        serviceStatus.Arguments.Add(unitArgument);
        serviceStatus.SetAction((p, ct) => RunOnNode(p, async api =>
        {
            var s = await api.GetServiceAsync(p.GetValue(unitArgument)!, ct);
            if (p.GetValue(jsonOption)) { ConsoleUi.WriteJson(s); return; }
            Console.WriteLine($"{s.Status.Name}: {s.Status.Description}");
            Console.WriteLine($"  状態    : {s.Status.ActiveState} ({s.Status.SubState})、PID {s.Status.MainPid?.ToString() ?? "-"}、開始 {Formatting.DateTime(s.Status.ActiveSince)}");
            Console.WriteLine($"  自動起動: {s.Status.UnitFileState}");
            Console.WriteLine($"  操作    : {(s.Operable ? "可" : "不可（管理対象外）")}");
        }));
        service.Subcommands.Add(serviceStatus);
        foreach (var action in Enum.GetValues<SystemAgent.Core.CapabilityProviders.ServiceAction>())
        {
            var (description, done) = action switch
            {
                SystemAgent.Core.CapabilityProviders.ServiceAction.Start => ("サービスを起動する", "を起動しました"),
                SystemAgent.Core.CapabilityProviders.ServiceAction.Stop => ("サービスを停止する", "を停止しました"),
                SystemAgent.Core.CapabilityProviders.ServiceAction.Restart => ("サービスを再起動する", "を再起動しました"),
                SystemAgent.Core.CapabilityProviders.ServiceAction.Enable => ("サービスの自動起動を有効化する", "の自動起動を有効化しました"),
                _ => ("サービスの自動起動を無効化する", "の自動起動を無効化しました"),
            };
            var command = new Command(action.ToString().ToLowerInvariant(), description);
            command.Arguments.Add(unitArgument);
            command.SetAction((p, ct) => RunOnNode(p, async api =>
            {
                await api.ServiceActionAsync(p.GetValue(unitArgument)!, action, ct);
                Console.WriteLine($"{p.GetValue(unitArgument)} {done}。");
            }));
            service.Subcommands.Add(command);
        }
        var linesOption = new Option<int>("--lines", "-n") { Description = "表示する行数", DefaultValueFactory = _ => 100 };
        var serviceLogs = new Command("logs", "サービスのログ（journal）を表示する");
        serviceLogs.Arguments.Add(unitArgument);
        serviceLogs.Options.Add(linesOption);
        serviceLogs.SetAction((p, ct) => RunOnNode(p, async api =>
            Console.Write((await api.GetServiceLogsAsync(p.GetValue(unitArgument)!, p.GetValue(linesOption), ct)).Logs)));
        service.Subcommands.Add(serviceLogs);
        root.Subcommands.Add(service);
    }
}
