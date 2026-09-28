namespace SystemAgent.Core.Errors;

/// <summary>
/// 利用者に原因をそのまま伝えるべき失敗の種類。APIでは ApiExceptionHandler がHTTPステータスに対応させる
/// （InvalidInput=400、NotFound=404、Conflict=409、TooLarge=413、OperationFailed=422、NotSupported=501、Unreachable=502、
/// InsufficientStorage=507）。
/// </summary>
public enum ErrorKind
{
    InvalidInput,
    NotFound,
    Conflict,
    OperationFailed,
    NotSupported,
    Unreachable,
    TooLarge,
    InsufficientStorage,
}

/// <summary>
/// SystemAgent の業務上の失敗。メッセージは利用者向け（日本語、対処が分かる内容）にする。
/// 新しい失敗を追加するときはこのクラスを継承し、Kind を決めるだけでAPIの応答に反映される。
/// </summary>
public abstract class SystemAgentException(string message, Exception? inner = null) : Exception(message, inner)
{
    public abstract ErrorKind Kind { get; }
}

/// <summary>この環境では該当機能を提供できない（ツール未導入・対応テンプレートなし等）。</summary>
public sealed class CapabilityUnavailableException(string message) : SystemAgentException(message)
{
    public override ErrorKind Kind => ErrorKind.NotSupported;
}

/// <summary>OSコマンドが失敗した。StandardErrorはコマンドのエラーメッセージ。</summary>
public sealed class CommandFailedException(string command, int exitCode, string standardError)
    : SystemAgentException($"コマンドが失敗しました（終了コード {exitCode}）: {standardError.Trim()}")
{
    public string Command { get; } = command;
    public int ExitCode { get; } = exitCode;
    public string StandardError { get; } = standardError;
    public override ErrorKind Kind => ErrorKind.OperationFailed;
}

/// <summary>このノードのDB（HAの昇格・降格用の接続）に対する操作が失敗した。中央DBの停止（503）とは区別する。</summary>
public sealed class DatabaseOperationException(string message, Exception? inner = null) : SystemAgentException(message, inner)
{
    public override ErrorKind Kind => ErrorKind.OperationFailed;
}

/// <summary>コンテナレジストリへの要求が失敗した。Unreachable は接続できなかった場合、それ以外はレジストリのエラー。</summary>
public sealed class RegistryRequestException(string message, bool unreachable = false) : SystemAgentException(message)
{
    public bool Unreachable { get; } = unreachable;
    public override ErrorKind Kind => Unreachable ? ErrorKind.Unreachable : ErrorKind.OperationFailed;
}

/// <summary>デプロイに失敗した（元のコンテナに戻した）。</summary>
public sealed class DeploymentFailedException(string message) : SystemAgentException(message)
{
    public override ErrorKind Kind => ErrorKind.OperationFailed;
}

/// <summary>要求の内容が規則に反する（最後のユーザーの削除、現在のパスワードの誤り等）。</summary>
public sealed class InvalidRequestException(string message) : SystemAgentException(message)
{
    public override ErrorKind Kind => ErrorKind.InvalidInput;
}

/// <summary>送られたファイルが上限を超えている。</summary>
public sealed class PayloadTooLargeException(string message) : SystemAgentException(message)
{
    public override ErrorKind Kind => ErrorKind.TooLarge;
}

/// <summary>保存先のディスクの空き容量が足りない。</summary>
public sealed class InsufficientStorageException(string message) : SystemAgentException(message)
{
    public override ErrorKind Kind => ErrorKind.InsufficientStorage;
}

/// <summary>対象が見つからない（メッセージで何が無いかを伝える）。</summary>
public sealed class NotFoundException(string message) : SystemAgentException(message)
{
    public override ErrorKind Kind => ErrorKind.NotFound;
}

/// <summary>現在の状態では要求された操作ができない（クラスタに参加済み・マスターなのでレプリカにできない・デプロイ中等）。</summary>
public sealed class ClusterStateException(string message) : SystemAgentException(message)
{
    public override ErrorKind Kind => ErrorKind.Conflict;
}

/// <summary>他ノードへ転送できなかった（クラスタ未参加・ノード未登録・接続不可）。</summary>
public sealed class NodeForwardException(ErrorKind kind, string message) : SystemAgentException(message)
{
    public override ErrorKind Kind => kind;
}
