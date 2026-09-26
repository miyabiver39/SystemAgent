using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SystemAgent.Core.CapabilityProviders;

namespace SystemAgent.Infrastructure.CapabilityProviders.Containers;

/// <summary>
/// コンテナランタイムの出力パーサー。テンプレートの "parser" から名前で参照される。
/// ランタイム固有の出力形式の差分はここに隔離する（基本設計書 7.2節）。
/// </summary>
public static partial class ContainerOutputParsers
{
    public static readonly IReadOnlyDictionary<string, Func<string, IReadOnlyList<ContainerInfo>>> Containers =
        new Dictionary<string, Func<string, IReadOnlyList<ContainerInfo>>>
        {
            ["podman-containers-json"] = ParsePodmanContainers,
            ["docker-containers-jsonl"] = ParseDockerContainers,
        };

    public static readonly IReadOnlyDictionary<string, Func<string, IReadOnlyList<ImageInfo>>> Images =
        new Dictionary<string, Func<string, IReadOnlyList<ImageInfo>>>
        {
            ["podman-images-json"] = ParsePodmanImages,
            ["docker-images-jsonl"] = ParseDockerImages,
        };

    public static readonly IReadOnlyDictionary<string, Func<string, IReadOnlyList<PodInfo>>> Pods =
        new Dictionary<string, Func<string, IReadOnlyList<PodInfo>>>
        {
            ["podman-pods-json"] = ParsePodmanPods,
        };

    // --- Podman（`--format json` はJSON配列） ---

    private static IReadOnlyList<ContainerInfo> ParsePodmanContainers(string output) =>
        JsonArray(output).Select(c => new ContainerInfo(
            Id: c.GetProperty("Id").GetString()!,
            Name: c.TryGetProperty("Names", out var names) && names.ValueKind == JsonValueKind.Array && names.GetArrayLength() > 0
                ? names[0].GetString()! : "",
            Image: String(c, "Image") ?? "",
            State: ParseState(String(c, "State") ?? ""),
            Status: String(c, "Status") ?? "",
            CreatedAt: c.TryGetProperty("Created", out var created) && created.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeSeconds(created.GetInt64()) : null,
            Pod: NullIfEmpty(String(c, "PodName")),
            IsInfra: c.TryGetProperty("IsInfra", out var infra) && infra.ValueKind == JsonValueKind.True)).ToList();

    private static IReadOnlyList<ImageInfo> ParsePodmanImages(string output) =>
        JsonArray(output).Select(i => new ImageInfo(
            Id: i.GetProperty("Id").GetString()!,
            Tags: StringArray(i, "Names") ?? StringArray(i, "RepoTags") ?? [],
            SizeBytes: i.TryGetProperty("Size", out var size) && size.ValueKind == JsonValueKind.Number ? size.GetInt64() : null,
            CreatedAt: i.TryGetProperty("Created", out var created) && created.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeSeconds(created.GetInt64()) : null)).ToList();

    private static IReadOnlyList<PodInfo> ParsePodmanPods(string output) =>
        JsonArray(output).Select(p => new PodInfo(
            Id: p.GetProperty("Id").GetString()!,
            Name: String(p, "Name") ?? "",
            Status: String(p, "Status") ?? "",
            ContainerCount: p.TryGetProperty("Containers", out var containers) && containers.ValueKind == JsonValueKind.Array
                ? containers.GetArrayLength() : 0,
            CreatedAt: DateTimeOffset.TryParse(String(p, "Created"), CultureInfo.InvariantCulture, out var created)
                ? created : null)).ToList();

    // --- Docker（`--format '{{json .}}'` は1行1オブジェクト） ---

    private static IReadOnlyList<ContainerInfo> ParseDockerContainers(string output) =>
        JsonLines(output).Select(c =>
        {
            var status = String(c, "Status") ?? "";
            // StateはDocker 20.10未満の出力には無いため、Statusから推定する
            var state = String(c, "State") is { Length: > 0 } s ? ParseState(s) : StateFromDockerStatus(status);
            return new ContainerInfo(
                Id: c.GetProperty("ID").GetString()!,
                Name: (String(c, "Names") ?? "").Split(',')[0],
                Image: String(c, "Image") ?? "",
                State: state,
                Status: status,
                CreatedAt: ParseDockerTimestamp(String(c, "CreatedAt")),
                Pod: null,
                IsInfra: false);
        }).ToList();

    private static IReadOnlyList<ImageInfo> ParseDockerImages(string output) =>
        JsonLines(output).Select(i =>
        {
            var repository = String(i, "Repository") ?? "<none>";
            var tag = String(i, "Tag") ?? "<none>";
            return new ImageInfo(
                Id: i.GetProperty("ID").GetString()!,
                Tags: repository == "<none>" ? [] : [tag == "<none>" ? repository : $"{repository}:{tag}"],
                SizeBytes: ParseHumanSize(String(i, "Size")),
                CreatedAt: ParseDockerTimestamp(String(i, "CreatedAt")));
        }).ToList();

    // --- 共通 ---

    public static ContainerState ParseState(string state) => state.ToLowerInvariant() switch
    {
        "running" => ContainerState.Running,
        "created" or "configured" or "initialized" => ContainerState.Created,
        "exited" or "stopped" => ContainerState.Exited,
        "paused" => ContainerState.Paused,
        "restarting" => ContainerState.Restarting,
        "removing" => ContainerState.Removing,
        "dead" => ContainerState.Dead,
        _ => ContainerState.Unknown,
    };

    private static ContainerState StateFromDockerStatus(string status) => status switch
    {
        _ when status.StartsWith("Up", StringComparison.Ordinal) && status.Contains("(Paused)") => ContainerState.Paused,
        _ when status.StartsWith("Up", StringComparison.Ordinal) => ContainerState.Running,
        _ when status.StartsWith("Exited", StringComparison.Ordinal) => ContainerState.Exited,
        _ when status.StartsWith("Created", StringComparison.Ordinal) => ContainerState.Created,
        _ when status.StartsWith("Restarting", StringComparison.Ordinal) => ContainerState.Restarting,
        _ when status.StartsWith("Removal", StringComparison.Ordinal) => ContainerState.Removing,
        _ when status.StartsWith("Dead", StringComparison.Ordinal) => ContainerState.Dead,
        _ => ContainerState.Unknown,
    };

    /// <summary>Dockerの "2026-09-26 13:48:37 +0900 JST" 形式。</summary>
    public static DateTimeOffset? ParseDockerTimestamp(string? text)
    {
        var match = text is null ? null : DockerTimestamp().Match(text);
        if (match is null || !match.Success) return null;

        var local = DateTime.ParseExact(match.Groups["dt"].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var offset = new TimeSpan(int.Parse(match.Groups["h"].Value), int.Parse(match.Groups["m"].Value), 0);
        return new DateTimeOffset(local, match.Groups["sign"].Value == "-" ? -offset : offset);
    }

    /// <summary>Dockerの "13MB" "4.1kB" 等（10進SI単位）をバイト数にする。</summary>
    public static long? ParseHumanSize(string? text)
    {
        var match = text is null ? null : HumanSize().Match(text);
        if (match is null || !match.Success) return null;

        var multiplier = match.Groups["unit"].Value switch
        {
            "B" => 1d,
            "kB" or "KB" => 1e3,
            "MB" => 1e6,
            "GB" => 1e9,
            "TB" => 1e12,
            _ => 1d,
        };
        return (long)(double.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture) * multiplier);
    }

    private static IEnumerable<JsonElement> JsonArray(string output) =>
        string.IsNullOrWhiteSpace(output) ? [] : JsonDocument.Parse(output).RootElement.EnumerateArray().Select(e => e.Clone()).ToList();

    private static IEnumerable<JsonElement> JsonLines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToList();

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string[]? StringArray(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(v => v.GetString()!).ToArray()
            : null;

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    [GeneratedRegex(@"^(?<dt>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}) (?<sign>[+-])(?<h>\d{2})(?<m>\d{2})")]
    private static partial Regex DockerTimestamp();

    [GeneratedRegex(@"^(?<n>[\d.]+)\s*(?<unit>[kKMGT]?B)")]
    private static partial Regex HumanSize();
}
