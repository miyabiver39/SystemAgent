namespace SystemAgent.Core.Ha;

public enum VrrpState
{
    Unknown,
    Master,
    Backup,
    Fault,
    Stop,
}

public enum HaReturnMode
{
    /// <summary>元マスターが復帰したら、自動で現マスターのレプリカとして再参加させる。</summary>
    Auto,

    /// <summary>元マスターが復帰したら「復帰待ち」とし、管理者の承認で再参加させる。</summary>
    Manual,
}

public sealed record HaEvent(DateTimeOffset At, string Message);

/// <summary>このノードのMariaDBのレプリケーション状態。</summary>
/// <param name="ReplicationRunning">レプリカとして複製中（IO/SQLスレッドとも稼働）か。</param>
/// <param name="PrerequisiteProblems">フェイルオーバーの前提条件（log_bin・log_slave_updates・gtid_strict_mode）の不足。空なら問題なし。</param>
public sealed record DbRoleStatus(
    bool Reachable,
    string? Error,
    bool ReadOnly,
    bool IsReplica,
    bool ReplicationRunning,
    string? SourceHost,
    int? SourcePort,
    int? SecondsBehindSource,
    string? LastError,
    string? GtidPosition,
    int? ServerId = null,
    IReadOnlyList<string>? PrerequisiteProblems = null);
