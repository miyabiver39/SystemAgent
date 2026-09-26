using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SystemAgent.Infrastructure.Ha;

/// <summary>
/// keepalived.conf の生成（ADR-022）。ファイル全体をSystemAgentが管理する。
/// 全ノード state BACKUP + nopreempt とし、復帰したノードがVIPを奪い返さないようにする（復帰は自動/手動モードで制御）。
/// 状態変化は notify で SystemAgent に通知し、昇格・降格の処理はSystemAgent内で行う（DB接続情報をスクリプトに書かない）。
/// </summary>
public static partial class KeepalivedConfig
{
    public const string InstanceName = "SYSTEMAGENT";
    public const string NotifyCommand = "/usr/bin/systemagent ha notify";

    public static string Generate(HaSettings settings, string nodeName)
    {
        Validate(settings);
        var sb = new StringBuilder();
        sb.Append("# Managed by SystemAgent (do not edit). 変更はSystemAgentのHA設定から行ってください。\n");
        sb.Append("global_defs {\n");
        sb.Append($"    router_id {Sanitize(nodeName)}\n");
        sb.Append("    script_user root\n");
        sb.Append("    enable_script_security\n");
        sb.Append("}\n\n");
        sb.Append($"vrrp_instance {InstanceName} {{\n");
        sb.Append("    state BACKUP\n");
        sb.Append("    nopreempt\n");
        sb.Append($"    interface {settings.Interface}\n");
        sb.Append($"    virtual_router_id {settings.VirtualRouterId}\n");
        sb.Append($"    priority {settings.Priority}\n");
        sb.Append("    advert_int 1\n");
        if (settings.AuthPass is { Length: > 0 } pass)
        {
            sb.Append("    authentication {\n");
            sb.Append("        auth_type PASS\n");
            sb.Append($"        auth_pass {pass}\n");
            sb.Append("    }\n");
        }
        if (settings.UnicastPeers.Count > 0)
        {
            if (settings.UnicastSourceIp is { Length: > 0 } source) sb.Append($"    unicast_src_ip {source}\n");
            sb.Append("    unicast_peer {\n");
            foreach (var peer in settings.UnicastPeers) sb.Append($"        {peer}\n");
            sb.Append("    }\n");
        }
        sb.Append("    virtual_ipaddress {\n");
        sb.Append($"        {settings.VirtualIp} dev {settings.Interface}\n");
        sb.Append("    }\n");
        foreach (var state in new[] { "master", "backup", "fault", "stop" })
        {
            sb.Append($"    notify_{state} \"{NotifyCommand} {state.ToUpperInvariant()}\"\n");
        }
        sb.Append("}\n");
        return sb.ToString();
    }

    /// <summary>設定ファイルに埋め込む値の検証。改行や引用符で設定を壊されないよう、形式を厳密に確認する。</summary>
    public static void Validate(HaSettings s)
    {
        if (!InterfaceName().IsMatch(s.Interface)) throw new ArgumentException($"インターフェース名が不正です: {s.Interface}");
        var parts = s.VirtualIp.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var vip) || !int.TryParse(parts[1], out var prefix)
            || prefix < 1 || prefix > (vip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32))
            throw new ArgumentException($"VIPはCIDR形式で指定してください（例: 10.0.0.100/24）: {s.VirtualIp}");
        if (s.VirtualRouterId is < 1 or > 255) throw new ArgumentException("virtual_router_id は1〜255で指定してください。");
        if (s.Priority is < 1 or > 254) throw new ArgumentException("priority は1〜254で指定してください。");
        if (s.AuthPass is { Length: > 0 } pass && (pass.Length > 8 || !AuthPass().IsMatch(pass)))
            throw new ArgumentException("認証パスワードは英数字8文字以内で指定してください（keepalivedの制約）。");
        foreach (var ip in s.UnicastPeers.Append(s.UnicastSourceIp).Where(ip => !string.IsNullOrEmpty(ip)))
        {
            if (!IPAddress.TryParse(ip, out _)) throw new ArgumentException($"ユニキャストのIPアドレスが不正です: {ip}");
        }
    }

    private static string Sanitize(string value) => NotSafe().Replace(value, "_");

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.@-]{0,14}$")]
    private static partial Regex InterfaceName();

    [GeneratedRegex(@"^[A-Za-z0-9]+$")]
    private static partial Regex AuthPass();

    [GeneratedRegex(@"[^A-Za-z0-9_.-]")]
    private static partial Regex NotSafe();
}
