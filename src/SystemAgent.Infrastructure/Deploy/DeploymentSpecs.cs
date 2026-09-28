using System.Text.RegularExpressions;
using SystemAgent.Core.Deploy;
using SystemAgent.Infrastructure.CapabilityProviders.Containers;

namespace SystemAgent.Infrastructure.Deploy;

/// <summary>
/// アプリ定義（DeploymentSpec）の入力検証と、秘密情報らしい環境変数の伏せ字（ADR-024）。
/// 値はコマンドの引数（--publish= 等）にそのまま渡るため、形式を厳しく検証する。
/// </summary>
public static partial class DeploymentSpecs
{
    /// <summary>更新中に既存コンテナを退避する名前の接尾辞。アプリ名には使えない。</summary>
    public const string PreviousSuffix = "-previous";

    /// <summary>API・画面で秘密情報の値の代わりに返す文字列。これのまま保存すると保存済みの値を引き継ぐ。</summary>
    public const string Masked = "********";

    /// <summary>入力を検証し、空白の除去・空要素の削除をしたものを返す。不正なら ArgumentException。</summary>
    public static DeploymentSpec Validate(DeploymentSpec spec)
    {
        var name = spec.Name.Trim();
        if (!ContainerNames.IsValidContainerName(name) || name.EndsWith(PreviousSuffix, StringComparison.Ordinal))
            throw new ArgumentException($"コンテナ名が不正です（英数字と _ . - 、63文字以内、末尾 {PreviousSuffix} は不可）: {spec.Name}");
        var image = spec.Image.Trim();
        var lastSegment = image[(image.LastIndexOf('/') + 1)..];
        if (!ImagePattern().IsMatch(image) || lastSegment.Contains(':'))
            throw new ArgumentException($"イメージはタグなしで指定してください（例: registry.example.com/app/web）: {spec.Image}");
        if (spec.Restart is not ("no" or "always" or "unless-stopped" or "on-failure"))
            throw new ArgumentException("再起動ポリシーは no / always / unless-stopped / on-failure のいずれかです。");
        var pod = string.IsNullOrWhiteSpace(spec.Pod) ? null : spec.Pod.Trim();
        if (pod is not null && !ContainerNames.IsValidContainerName(pod)) throw new ArgumentException($"Pod名が不正です: {spec.Pod}");
        if (spec.HealthCheckSeconds is < 0 or > 600) throw new ArgumentException("動作確認の待ち時間は0〜600秒です。");

        var ports = Clean(spec.Ports, PortPattern(), "ポート公開（例: 8080:80、127.0.0.1:8080:80/tcp）");
        if (pod is not null && ports.Count > 0) throw new ArgumentException("Podに参加する場合、ポートはPod側で公開してください。");
        var environment = Clean(spec.Environment, EnvPattern(), "環境変数（KEY=VALUE）");
        if (environment.GroupBy(e => SplitEnv(e).Key).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
            throw new ArgumentException($"環境変数 {duplicate.Key} が重複しています。");
        var volumes = Clean(spec.Volumes, VolumePattern(), "ボリューム（/ホストのパス:/コンテナのパス[:ro] または 名前:/パス）");
        foreach (var volume in volumes) ValidateHostPath(volume);
        return spec with { Name = name, Image = image, Pod = pod, Ports = ports, Environment = environment, Volumes = volumes };
    }

    /// <summary>名前から秘密情報とみなす環境変数（値を画面・APIに出さない）。</summary>
    public static bool IsSecretKey(string key) => SecretKeyPattern().IsMatch(key);

    /// <summary>秘密情報らしい環境変数の値を伏せ字にする（画面・API向け）。</summary>
    public static DeploymentSpec MaskSecrets(DeploymentSpec spec) => spec with
    {
        Environment = spec.Environment.Select(e => SplitEnv(e) is var (key, _) && IsSecretKey(key) ? $"{key}={Masked}" : e).ToList(),
    };

    /// <summary>伏せ字のまま送られてきた環境変数を、保存済みの値に戻す。保存済みの値が無ければ ArgumentException。</summary>
    public static DeploymentSpec RestoreMaskedSecrets(DeploymentSpec spec, DeploymentSpec? saved) => spec with
    {
        Environment = spec.Environment.Select(e =>
        {
            var (key, value) = SplitEnv(e);
            if (value != Masked) return e;
            return saved?.Environment.FirstOrDefault(old => SplitEnv(old).Key == key)
                ?? throw new ArgumentException($"環境変数 {key} の値を入力してください。");
        }).ToList(),
    };

    /// <summary>
    /// ボリュームとしてマウントできないホストのディレクトリ（その配下も含む）。コンテナは root で起動するため、
    /// OSの設定・デバイス・コンテナランタイムのソケット、SystemAgent の秘密情報（master.key 等）・バックアップを
    /// コンテナから読み書きできないようにする。
    /// </summary>
    public static readonly IReadOnlyList<string> ProtectedHostPaths =
    [
        "/bin", "/boot", "/dev", "/etc", "/lib", "/lib64", "/proc", "/root", "/run", "/sbin", "/sys", "/usr",
        "/var/run", "/var/lib/systemagent", "/var/lib/containers", "/var/lib/docker",
    ];

    private static void ValidateHostPath(string volume)
    {
        var host = volume[..volume.IndexOf(':')];
        if (!host.StartsWith('/')) return; // 名前付きボリューム

        var segments = host.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s is "." or ".."))
            throw new ArgumentException($"ボリュームのホスト側のパスに . や .. は使えません: {volume}");
        var normalized = "/" + string.Join('/', segments);
        if (normalized == "/")
            throw new ArgumentException($"ホストのルートディレクトリ（/）はボリュームにできません: {volume}");
        if (ProtectedHostPaths.FirstOrDefault(p => normalized == p || normalized.StartsWith(p + "/", StringComparison.Ordinal)) is { } denied)
            throw new ArgumentException($"ホストの {denied} とその配下はボリュームにできません（OSとSystemAgentの秘密情報を保護するため）: {volume}");
    }

    private static (string Key, string Value) SplitEnv(string entry)
    {
        var index = entry.IndexOf('=');
        return index < 0 ? (entry, "") : (entry[..index], entry[(index + 1)..]);
    }

    private static List<string> Clean(IReadOnlyList<string> values, Regex pattern, string label)
    {
        var cleaned = values.Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
        if (cleaned.FirstOrDefault(v => v.Length > 4096 || !pattern.IsMatch(v)) is { } invalid)
            throw new ArgumentException($"{label} の指定が不正です: {invalid}");
        return cleaned;
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._/:-]{0,254}$")]
    private static partial Regex ImagePattern();

    [GeneratedRegex(@"^((\d{1,3}\.){3}\d{1,3}:)?(\d{1,5}(-\d{1,5})?:)?\d{1,5}(-\d{1,5})?(/(tcp|udp|sctp))?$")]
    private static partial Regex PortPattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*=[^\x00-\x08\x0A-\x1F\x7F]*$")]
    private static partial Regex EnvPattern();

    [GeneratedRegex(@"^(/[^:\x00-\x1F]*|[A-Za-z0-9][A-Za-z0-9_.-]*):/[^:\x00-\x1F]*(:[A-Za-z,]+)?$")]
    private static partial Regex VolumePattern();

    [GeneratedRegex(@"(PASS|SECRET|TOKEN|KEY|CREDENTIAL)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretKeyPattern();
}
