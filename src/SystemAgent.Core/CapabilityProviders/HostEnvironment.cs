namespace SystemAgent.Core.CapabilityProviders;

/// <summary>
/// 環境検出の結果（基本設計書 7章 EnvironmentDetector）。コマンドテンプレートの選択に使う。
/// </summary>
/// <param name="OsId">/etc/os-release の ID（例: almalinux, ubuntu）。</param>
/// <param name="OsIdLike">/etc/os-release の ID_LIKE（例: rhel centos fedora）。</param>
public sealed record HostEnvironment(
    string HostName,
    string OsId,
    IReadOnlyList<string> OsIdLike,
    string OsVersion,
    string OsPrettyName,
    string Architecture,
    IReadOnlyList<ToolInfo> Tools)
{
    public ToolInfo? FindTool(string name) => Tools.FirstOrDefault(t => t.Name == name);
}

public sealed record ToolInfo(string Name, string Version);

public interface IEnvironmentDetector
{
    Task<HostEnvironment> DetectAsync(bool refresh = false, CancellationToken cancellationToken = default);
}

/// <summary>この環境では該当機能を提供できない（ツール未導入・対応テンプレートなし等）。</summary>
public sealed class CapabilityUnavailableException(string message) : Exception(message);

/// <summary>OSコマンドが失敗した。StandardErrorはランタイムのエラーメッセージ。</summary>
public sealed class CommandFailedException(string command, int exitCode, string standardError)
    : Exception($"コマンドが失敗しました（終了コード {exitCode}）: {standardError.Trim()}")
{
    public string Command { get; } = command;
    public int ExitCode { get; } = exitCode;
    public string StandardError { get; } = standardError;
}
