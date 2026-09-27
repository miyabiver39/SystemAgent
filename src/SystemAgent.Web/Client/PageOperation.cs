using SystemAgent.Client;

namespace SystemAgent.Web.Client;

/// <summary>
/// 画面の操作（ボタン等）の共通処理: 実行中の表示、エラー・完了メッセージ、成功後の再読み込み。
/// 表示は OperationNotices コンポーネントで行う。API のエラー（ApiException）と通信の失敗（接続できない・タイムアウト）は
/// 画面に表示し、それ以外は既定のエラー処理に任せる。
/// </summary>
public sealed class PageOperation
{
    public const string ConnectionFailedMessage = "サーバーに接続できません。サーバーが起動しているか、ネットワークを確認してください。";
    public const string TimeoutMessage = "処理がタイムアウトしました。時間をおいて状態を確認してください。";

    /// <summary>操作の実行中（ボタンを無効にする等に使う）。</summary>
    public bool Busy { get; set; }

    public string? Error { get; set; }

    /// <summary>完了メッセージ。操作の中で設定する。</summary>
    public string? Message { get; set; }

    /// <param name="action">操作。完了メッセージは Message に設定する。</param>
    /// <param name="reload">成功後に画面を読み込み直す処理（完了メッセージは残す）。</param>
    /// <param name="reloadOnFailure">失敗しても読み込み直す（失敗の履歴を表示する画面など）。</param>
    /// <param name="describeError">APIのエラーを表示用の文に直す（省略時はエラーの内容そのまま）。</param>
    /// <remarks>
    /// 実行中に呼ばれた場合は何もしない。ボタンの disabled は再描画されるまで反映されないため、
    /// 素早く2回押されると同じ操作（停止・デプロイ・バックアップ等）が並行して実行されてしまうのを防ぐ。
    /// </remarks>
    public async Task RunAsync(Func<Task> action, Func<Task>? reload = null, bool reloadOnFailure = false,
        Func<ApiException, string>? describeError = null)
    {
        // Blazor Server の画面処理は回線ごとに1スレッドずつ実行されるため、確認と設定の間に割り込まれない
        if (Busy) return;
        Busy = true;
        Error = null;
        Message = null;
        bool succeeded;
        try
        {
            succeeded = await TryAsync(action, describeError);
        }
        finally
        {
            Busy = false;
        }

        if (reload is not null && (succeeded || reloadOnFailure))
        {
            var (message, error) = (Message, Error);
            await TryAsync(reload, describeError);
            Message = message;
            Error = error ?? Error;
        }
    }

    private async Task<bool> TryAsync(Func<Task> action, Func<ApiException, string>? describeError)
    {
        try
        {
            await action();
            return true;
        }
        catch (ApiException ex)
        {
            Error = describeError?.Invoke(ex) ?? ex.Message;
        }
        catch (HttpRequestException)
        {
            // サーバーの停止・再起動、ネットワーク切断など
            Error = ConnectionFailedMessage;
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // HttpClient のタイムアウトは TaskCanceledException になる
            Error = TimeoutMessage;
        }
        return false;
    }
}
