using System.CommandLine;
using SystemAgent.Client;

namespace SystemAgent.Cli.Commands;

/// <summary>ホストネットワーク（network）</summary>
internal static class NetworkCommands
{
    public static void Register(RootCommand root, CliContext cli)
    {
        var jsonOption = cli.JsonOption;
        Task<int> RunOnNode(ParseResult p, Func<ApiClient, Task> action) => cli.RunOnNode(p, action);

        var network = new Command("network", "ホストネットワーク（このノード。現在は参照のみ）");
        var allOption = new Option<bool>("--all", "-a") { Description = "ループバック・コンテナ用のインターフェースとリンクローカルの経路も表示する" };
        var networkShow = new Command("show", "インターフェース・経路・DNSを表示する");
        networkShow.Options.Add(allOption);
        networkShow.SetAction((p, ct) => RunOnNode(p, async api =>
        {
            var n = await api.GetNetworkAsync(ct);
            if (p.GetValue(jsonOption)) { ConsoleUi.WriteJson(n); return; }
            var hidden = n.Interfaces.Where(i => i.ContainerNetwork || i.Kind == SystemAgent.Core.CapabilityProviders.InterfaceKind.Loopback).Select(i => i.Name).ToHashSet();
            var interfaces = n.Interfaces.Where(i => p.GetValue(allOption) || (!i.ContainerNetwork && i.Kind != SystemAgent.Core.CapabilityProviders.InterfaceKind.Loopback)).ToList();
            ConsoleUi.WriteTable(["名前", "種別", "状態", "MTU", "MAC", "アドレス"],
                interfaces.Select(i => (IReadOnlyList<string>)
                [
                    i.Name + (i.Master is { } m ? $" (@{m})" : ""), i.Kind.ToString(), i.State, i.Mtu?.ToString() ?? "", i.MacAddress ?? "",
                    string.Join(", ", i.Addresses.Where(a => !a.Address.StartsWith("fe80")).Select(a => a.ToString() + (a.Dynamic ? " (DHCP)" : ""))),
                ]));
            Console.WriteLine();
            ConsoleUi.WriteTable(["宛先", "ゲートウェイ", "デバイス", "プロトコル"],
                n.Routes.Where(r => p.GetValue(allOption) || (!r.Destination.StartsWith("fe80") && !hidden.Contains(r.Device ?? "")))
                    .Select(r => (IReadOnlyList<string>)[r.Destination, r.Gateway ?? "", r.Device ?? "", r.Protocol ?? ""]));
            Console.WriteLine();
            Console.WriteLine($"DNSサーバー : {(n.DnsServers.Count == 0 ? "-" : string.Join(", ", n.DnsServers))}");
            Console.WriteLine($"検索ドメイン: {(n.SearchDomains.Count == 0 ? "-" : string.Join(", ", n.SearchDomains))}");
            if (!p.GetValue(allOption)) Console.Error.WriteLine("（ループバックとコンテナ用のインターフェースは省略。--all で表示）");
        }));
        network.Subcommands.Add(networkShow);
        root.Subcommands.Add(network);
    }
}
