using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using SystemAgent.Core.Contracts;
using SystemAgent.Infrastructure.CapabilityProviders.Templates;
using SystemAgent.Infrastructure.Commands;
using SystemAgent.Infrastructure.Persistence;
using SystemAgent.Core.Errors;

namespace SystemAgent.Infrastructure.Backup;

/// <summary>
/// 中央DBのバックアップ（非機能要件「バックアップ機能必須」、ADR-010「帯域制御はアプリ側」）。
/// ダンプコマンドの出力を速度制限しながらgzip圧縮して保存する。ファイルはこのノードのディレクトリに置く（NFS等のマウント先も可）。
/// </summary>
public sealed partial class BackupService(
    CapabilityTemplateResolver resolver, ICommandRunner runner, DatabaseConnection connection,
    IConfiguration configuration, TimeProvider time, ILogger<BackupService> logger)
{
    public const string Capability = "db-backup";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromHours(6);

    public string Directory => configuration["Backup:Directory"] is { Length: > 0 } dir
        ? dir
        : OperatingSystem.IsLinux() ? "/var/lib/systemagent/backups" : Path.Combine(Path.GetTempPath(), "systemagent-backups");

    public int Retention => Math.Max(1, configuration.GetValue("Backup:Retention", 7));

    /// <summary>既定 10MB/秒。0で無制限。</summary>
    public int RateLimitKBps => Math.Max(0, configuration.GetValue("Backup:RateLimitKBps", 10240));

    public string? DailyAt => configuration["Backup:DailyAt"] is { Length: > 0 } at ? at : null;

    public async Task<BackupSettingsResponse> GetSettingsAsync(CancellationToken cancellationToken)
    {
        string? tool;
        try
        {
            tool = (await ResolveAsync(cancellationToken)).Tool.Name;
        }
        catch (CapabilityUnavailableException)
        {
            tool = null;
        }
        return new BackupSettingsResponse(Directory, Retention, RateLimitKBps, DailyAt, tool);
    }

    public IReadOnlyList<BackupFileInfo> List()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        return new DirectoryInfo(Directory).EnumerateFiles("systemagent-*.sql.gz")
            .Where(f => FileName().IsMatch(f.Name))
            .Select(f => new BackupFileInfo(f.Name, f.Length, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero)))
            .OrderByDescending(f => f.Name)
            .ToList();
    }

    public async Task<BackupFileInfo> CreateAsync(CancellationToken cancellationToken)
    {
        var (executor, _) = await ResolveAsync(cancellationToken);
        var (connectionString, _) = connection.Current;
        if (connectionString is null) throw new CapabilityUnavailableException("DB接続が設定されていません。");
        var database = new MySqlConnectionStringBuilder(connectionString).Database;

        CreateDirectory(Directory);
        var name = $"systemagent-{time.GetUtcNow().ToLocalTime():yyyyMMdd-HHmmss}.sql.gz";
        var path = Path.Combine(Directory, name);
        var partial = path + ".partial";

        using var defaults = OptionFile.Create(connectionString);
        try
        {
            CommandResult result;
            await using (var file = OpenPrivate(partial))
            await using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
            await using (var throttled = new ThrottledStream(gzip, RateLimitKBps * 1024L, time))
            {
                var command = executor.Template.Commands["dump"];
                var args = command.Render(Values(executor, defaults.Path, database));
                logger.LogInformation("バックアップを開始します: {Name}（上限 {Rate} KB/秒）", name, RateLimitKBps);
                result = await runner.RunStreamingAsync(command.Executable ?? executor.Template.Executable, args, null, throttled, CommandTimeout, cancellationToken);
            }
            if (result.ExitCode != 0) throw new CommandFailedException("dump", result.ExitCode, result.StandardError);
        }
        catch
        {
            File.Delete(partial);
            throw;
        }
        File.Move(partial, path);
        Prune();

        var info = new FileInfo(path);
        logger.LogInformation("バックアップが完了しました: {Name}（{Bytes} bytes）", name, info.Length);
        return new BackupFileInfo(name, info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
    }

    public Stream OpenRead(string name) => File.OpenRead(PathOf(name));

    public bool Delete(string name)
    {
        var path = PathOf(name);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    /// <summary>バックアップでDBを置き換える。</summary>
    public async Task RestoreAsync(string name, CancellationToken cancellationToken)
    {
        var path = PathOf(name);
        if (!File.Exists(path)) throw new FileNotFoundException("バックアップが見つかりません。", name);
        var (executor, _) = await ResolveAsync(cancellationToken);
        var (connectionString, _) = connection.Current;
        if (connectionString is null) throw new CapabilityUnavailableException("DB接続が設定されていません。");

        using var defaults = OptionFile.Create(connectionString);
        await using var file = File.OpenRead(path);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        await using var throttled = new ThrottledStream(gzip, RateLimitKBps * 1024L, time);

        var command = executor.Template.Commands["restore"];
        var args = command.Render(Values(executor, defaults.Path, new MySqlConnectionStringBuilder(connectionString).Database));
        logger.LogWarning("バックアップ {Name} からDBを復元します。", name);
        var result = await runner.RunStreamingAsync(command.Executable ?? executor.Template.Executable, args, throttled, null, CommandTimeout, cancellationToken);
        if (result.ExitCode != 0) throw new CommandFailedException("restore", result.ExitCode, result.StandardError);
    }

    /// <summary>保持数を超えた古いバックアップを消す。</summary>
    public void Prune()
    {
        foreach (var old in List().Skip(Retention))
        {
            File.Delete(Path.Combine(Directory, old.Name));
            logger.LogInformation("保持数（{Retention}）を超えた古いバックアップを削除しました: {Name}", Retention, old.Name);
        }
    }

    public static bool IsValidName(string name) => FileName().IsMatch(name);

    private string PathOf(string name) =>
        IsValidName(name) ? Path.Combine(Directory, name) : throw new ArgumentException($"バックアップ名が不正です: {name}");

    private Task<CapabilityTemplateResolver.Resolution> ResolveAsync(CancellationToken cancellationToken) =>
        resolver.ResolveAsync(Capability, "Backup:Tool", ["mariadb-dump", "mysqldump"], "DBダンプツール", cancellationToken);

    private static Dictionary<string, string> Values(TemplateCommandExecutor executor, string defaultsPath, string database)
    {
        var values = new Dictionary<string, string>(executor.Template.Settings) { ["defaults"] = defaultsPath, ["database"] = database };
        return values;
    }

    private static void CreateDirectory(string directory)
    {
        if (OperatingSystem.IsWindows()) System.IO.Directory.CreateDirectory(directory);
        else System.IO.Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static FileStream OpenPrivate(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    [GeneratedRegex(@"^systemagent-\d{8}-\d{6}\.sql\.gz$")]
    private static partial Regex FileName();

    /// <summary>
    /// DBの接続情報を渡す一時オプションファイル（権限600）。パスワードをコマンドライン引数に出さないため。
    /// </summary>
    private sealed class OptionFile : IDisposable
    {
        public string Path { get; }

        private OptionFile(string path) => Path = path;

        public static OptionFile Create(string connectionString)
        {
            var c = new MySqlConnectionStringBuilder(connectionString);
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"systemagent-db-{Guid.NewGuid():N}.cnf");
            var text = new StringBuilder("[client]\n")
                .Append(CultureInfo.InvariantCulture, $"host={c.Server}\n")
                .Append(CultureInfo.InvariantCulture, $"port={c.Port}\n")
                .Append(CultureInfo.InvariantCulture, $"user={Quote(c.UserID)}\n")
                .Append(CultureInfo.InvariantCulture, $"password={Quote(c.Password)}\n")
                .ToString();
            using (var file = OpenPrivate(path)) file.Write(Encoding.UTF8.GetBytes(text));
            return new OptionFile(path);
        }

        public static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        public void Dispose() => File.Delete(Path);
    }
}
