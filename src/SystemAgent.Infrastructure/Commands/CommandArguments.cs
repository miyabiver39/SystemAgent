using System.Text.RegularExpressions;

namespace SystemAgent.Infrastructure.Commands;

/// <summary>
/// コマンドライン引数をログ・エラーメッセージに出すときに、秘密情報らしい値を伏せ字にする。
/// パスワードは原則として標準入力やオプションファイルで渡すが、デプロイの環境変数（--env=DB_PASSWORD=...）のように
/// 引数に載る値もあるため、表示する側で必ず通す。
/// </summary>
public static partial class CommandArguments
{
    public const string Masked = "***";

    /// <summary>「実行ファイル 引数...」の表示用文字列（秘密情報は伏せ字）。</summary>
    public static string Describe(string executable, IEnumerable<string> arguments) =>
        string.Join(' ', [executable, .. Mask(arguments)]);

    public static IEnumerable<string> Mask(IEnumerable<string> arguments)
    {
        var maskNext = false;
        foreach (var argument in arguments)
        {
            if (maskNext)
            {
                // --password <値> の値
                maskNext = false;
                yield return Masked;
            }
            else if (SecretFlag().IsMatch(argument))
            {
                maskNext = true;
                yield return argument;
            }
            else if (SecretAssignment().Match(argument) is { Success: true } match)
            {
                // --password=<値>、--env=DB_PASSWORD=<値>、DB_PASSWORD=<値>
                yield return match.Groups["name"].Value + "=" + Masked;
            }
            else
            {
                yield return argument;
            }
        }
    }

    // 値を次の引数に取るオプション（--password-stdin のような値を取らないものは含まない）
    [GeneratedRegex(@"^--?[A-Za-z0-9-]*(pass|passwd|password|secret|token|credential|credentials|api-?key)$", RegexOptions.IgnoreCase)]
    private static partial Regex SecretFlag();

    [GeneratedRegex(
        @"^(?<name>--?[A-Za-z0-9-]*(pass|secret|token|credential|key)[A-Za-z0-9-]*|(--?[A-Za-z0-9-]+=)?[A-Za-z_][A-Za-z0-9_.-]*(pass|secret|token|credential|key)[A-Za-z0-9_.-]*)=",
        RegexOptions.IgnoreCase)]
    private static partial Regex SecretAssignment();
}
