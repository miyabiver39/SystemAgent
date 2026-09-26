using System.Globalization;

namespace SystemAgent.Client;

/// <summary>WebUIとCLIで表示形式を揃えるための書式化。</summary>
public static class Formatting
{
    /// <summary>10進SI単位（Podman/Dockerの表示と同じ）。</summary>
    public static string Bytes(long? bytes) => bytes switch
    {
        null => "",
        < 1_000 => $"{bytes} B",
        < 1_000_000 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e3:0.#} kB"),
        < 1_000_000_000 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e6:0.#} MB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e9:0.##} GB"),
    };

    public static string ShortId(string id)
    {
        var s = id.StartsWith("sha256:", StringComparison.Ordinal) ? id[7..] : id;
        return s.Length > 12 ? s[..12] : s;
    }

    public static string DateTime(DateTimeOffset? value) => value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "";
}
