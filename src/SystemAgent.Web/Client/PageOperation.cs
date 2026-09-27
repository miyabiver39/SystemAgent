using SystemAgent.Client;

namespace SystemAgent.Web.Client;

/// <summary>
/// 画面の操作（ボタン等）の共通処理: 実行中の表示、エラー・完了メッセージ、成功後の再読み込み。
/// 表示は OperationNotices コンポーネントで行う。API のエラー（ApiException）は画面に表示し、それ以外は既定のエラー処理に任せる。
/// </summary>
public sealed class PageOperation
{
    /// <summary>操作の実行中（ボタンを無効にする等に使う）。</summary>
    public bool Busy { get; set; }

    public string? Error { get; set; }

    /// <summary>完了メッセージ。操作の中で設定する。</summary>
    public string? Message { get; set; }

    /// <param name="action">操作。完了メッセージは Message に設定する。</param>
    /// <param name="reload">成功後に画面を読み込み直す処理（完了メッセージは残す）。</param>
    /// <param name="reloadOnFailure">失敗しても読み込み直す（失敗の履歴を表示する画面など）。</param>
    /// <param name="describeError">APIのエラーを表示用の文に直す（省略時はエラーの内容そのまま）。</param>
    public async Task RunAsync(Func<Task> action, Func<Task>? reload = null, bool reloadOnFailure = false,
        Func<ApiException, string>? describeError = null)
    {
        Busy = true;
        Error = null;
        Message = null;
        var succeeded = false;
        try
        {
            await action();
            succeeded = true;
        }
        catch (ApiException ex)
        {
            Error = describeError?.Invoke(ex) ?? ex.Message;
        }
        finally
        {
            Busy = false;
        }

        if (reload is not null && (succeeded || reloadOnFailure))
        {
            var (message, error) = (Message, Error);
            await reload();
            Message = message;
            Error ??= error;
        }
    }
}
