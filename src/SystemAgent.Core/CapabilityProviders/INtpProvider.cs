namespace SystemAgent.Core.CapabilityProviders;

/// <summary>
/// 時刻同期（NTP）を抽象化するインターフェース（基本設計書 2.1節）。chrony / systemd-timesyncd の差分はテンプレートで吸収する。
/// </summary>
public interface INtpProvider
{
    /// <summary>使用中の実装（chrony / timesyncd）とテンプレート。</summary>
    NtpImplementationInfo Implementation { get; }

    Task<NtpStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>参照するNTPサーバーを置き換え、サービスを再起動して反映する。</summary>
    Task SetServersAsync(IReadOnlyList<string> servers, CancellationToken cancellationToken = default);

    /// <summary>即時に時刻を合わせる（chronyは段階調整せずステップ補正、timesyncdはサービス再起動）。</summary>
    Task SyncNowAsync(CancellationToken cancellationToken = default);
}

public sealed record NtpImplementationInfo(string Name, string TemplateId, string ConfigFile, string Service);

/// <param name="Synchronized">システム時刻が同期済みか。</param>
/// <param name="ConfiguredServers">設定ファイル上のサーバー（SystemAgentが設定したもの以外も含む）。</param>
/// <param name="CurrentSource">現在同期しているソース（未同期ならnull）。</param>
/// <param name="OffsetSeconds">参照時刻とのずれ（秒。正ならシステム時刻が進んでいる）。取得できない実装ではnull。</param>
public sealed record NtpStatus(
    string Implementation,
    bool Synchronized,
    IReadOnlyList<string> ConfiguredServers,
    string? CurrentSource,
    int? Stratum,
    double? OffsetSeconds,
    IReadOnlyList<NtpSource> Sources);

public enum NtpSourceState
{
    Unknown,
    /// <summary>同期に使用中（chronyの *）。</summary>
    Selected,
    /// <summary>候補として合成に使用（+）。</summary>
    Combined,
    /// <summary>候補外（-）。</summary>
    NotCombined,
    /// <summary>到達不能（?）。</summary>
    Unreachable,
    /// <summary>他のソースと矛盾（x）。</summary>
    Falseticker,
    /// <summary>ばらつきが大きい（~）。</summary>
    TooVariable,
}

public sealed record NtpSource(string Address, NtpSourceState State, int? Stratum, double? OffsetSeconds, bool Reachable);
