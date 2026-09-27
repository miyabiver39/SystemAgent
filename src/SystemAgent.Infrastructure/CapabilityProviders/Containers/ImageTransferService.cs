using SystemAgent.Core.CapabilityProviders;

namespace SystemAgent.Infrastructure.CapabilityProviders.Containers;

/// <summary>
/// レジストリとのイメージのやり取り（pull / push）。登録済みの認証情報でログインしてから行う（ADR-023、ADR-026）。
/// 画面・CLIからの取得とデプロイ時の取得で同じ手順を使う。
/// </summary>
public sealed class ImageTransferService(RegistryService registries)
{
    /// <summary>登録済みのレジストリなら先にログインしてから取得する（未登録なら匿名で取得）。</summary>
    public async Task PullAsync(IContainerRuntimeProvider runtime, string image, CancellationToken cancellationToken = default)
    {
        var tlsVerify = await registries.EnsureLoginAsync(runtime, image, cancellationToken);
        await runtime.PullImageAsync(image, tlsVerify, cancellationToken);
    }

    /// <summary>
    /// ローカルのイメージを送る。送り先の名前を一時的に付けて push し、元から無かった名前なら送信後に外す
    /// （ローカルのイメージ一覧に送り先の名前を残さない）。送り先のレジストリは登録済みであること。
    /// </summary>
    /// <param name="image">送るイメージ（名前またはID）。</param>
    /// <param name="target">送り先（レジストリ/リポジトリ:タグ）。</param>
    public async Task PushAsync(IContainerRuntimeProvider runtime, string image, string target, CancellationToken cancellationToken = default)
    {
        image = image.Trim();
        target = target.Trim();
        var registry = RegistryName.FromImage(target);
        if (registry == RegistryName.DockerHub && !target.StartsWith("docker.io/", StringComparison.Ordinal))
            throw new ArgumentException("送り先はレジストリを含めて指定してください（例: zot.example.com:5000/app/web:1.0）。");
        if (registries.Find(registry) is null)
            throw new ArgumentException($"レジストリ {registry} は登録されていません。先に「レジストリ」で登録してください。");

        var tlsVerify = await registries.EnsureLoginAsync(runtime, target, cancellationToken);
        var untagAfterPush = image != target
            && !await IsUntaggedAsync(runtime, image, cancellationToken)
            && !await runtime.ImageExistsAsync(target, cancellationToken);
        if (image != target) await runtime.TagImageAsync(image, target, cancellationToken);
        try
        {
            await runtime.PushImageAsync(target, tlsVerify, cancellationToken);
        }
        finally
        {
            // 送るためだけに付けた名前を外す（イメージ本体は元の名前で残る）。失敗時も外す
            if (untagAfterPush) await runtime.RemoveImageAsync(target, CancellationToken.None);
        }
    }

    /// <summary>
    /// 名前の無いイメージをIDで指定したか。その場合、付けた名前を外すとイメージ自体が消えるため名前を残す。
    /// </summary>
    private static async Task<bool> IsUntaggedAsync(IContainerRuntimeProvider runtime, string image, CancellationToken cancellationToken)
    {
        var id = image.Replace("sha256:", "");
        var source = (await runtime.ListImagesAsync(cancellationToken)).FirstOrDefault(i =>
            i.Tags.Contains(image) || i.Id.Replace("sha256:", "").StartsWith(id, StringComparison.Ordinal));
        return source is { Tags.Count: 0 };
    }
}
