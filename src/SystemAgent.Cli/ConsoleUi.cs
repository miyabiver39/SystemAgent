using System.Text;
using System.Text.Json;
using SystemAgent.Client;

namespace SystemAgent.Cli;

public static class ConsoleUi
{
    /// <summary>エコーなしでパスワードを読む。標準入力がリダイレクトされている場合は1行読む（スクリプト用）。</summary>
    public static string ReadSecret(string prompt)
    {
        Console.Error.Write(prompt);
        if (Console.IsInputRedirected)
        {
            var line = Console.ReadLine() ?? "";
            Console.Error.WriteLine();
            return line;
        }

        var buffer = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0) buffer.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
            }
        }
        Console.Error.WriteLine();
        return buffer.ToString();
    }

    public static string ReadNewPassword(int minLength)
    {
        while (true)
        {
            var password = ReadSecret("新しいパスワード: ");
            if (password.Length < minLength)
            {
                Console.Error.WriteLine($"パスワードは{minLength}文字以上で入力してください。");
            }
            else if (ReadSecret("新しいパスワード（確認）: ") != password)
            {
                Console.Error.WriteLine("確認用のパスワードが一致しません。");
            }
            else
            {
                return password;
            }
            if (Console.IsInputRedirected) throw new CliException("パスワードが条件を満たしていません。");
        }
    }

    public static string ReadLine(string prompt, string? defaultValue = null)
    {
        Console.Error.Write(defaultValue is null ? prompt : $"{prompt}[{defaultValue}] ");
        var line = Console.ReadLine()?.Trim();
        return string.IsNullOrEmpty(line) ? defaultValue ?? "" : line;
    }

    public static void WriteJson<T>(T value) =>
        Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions(ApiClient.Json) { WriteIndented = true }));

    /// <summary>全角文字を2桁として幅を揃えた表を出力する。</summary>
    public static void WriteTable(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        var all = new List<IReadOnlyList<string>> { headers };
        all.AddRange(rows);
        var widths = headers.Select((_, i) => all.Max(r => DisplayWidth(r[i]))).ToArray();
        foreach (var row in all)
        {
            Console.WriteLine(string.Join("  ", row.Select((cell, i) =>
                i == row.Count - 1 ? cell : cell + new string(' ', widths[i] - DisplayWidth(cell)))).TrimEnd());
        }
    }

    private static int DisplayWidth(string s) => s.Sum(c => c > 0xFF ? 2 : 1);
}

/// <summary>利用者向けのエラー。スタックトレースを出さずにメッセージだけ表示して終了する。</summary>
public sealed class CliException(string message) : Exception(message);
