using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SystemAgent.Core.Contracts;
using SystemAgent.Infrastructure.Persistence;
using SystemAgent.Infrastructure.Persistence.Entities;

namespace SystemAgent.Infrastructure.Auditing;

/// <summary>監査ログの検索・CSV出力（ADR-025）。中央DBに全ノードの記録が集まるため、どのノードからでも全体を見られる。</summary>
public sealed class AuditLogReader(AppDbContext db)
{
    public const int MaxPageSize = 500;
    public const int MaxExportRows = 100_000;

    public async Task<AuditLogPage> SearchAsync(AuditLogQuery query, CancellationToken cancellationToken = default)
    {
        var page = Math.Max(query.Page, 1);
        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);
        var filtered = Filter(query);
        var total = await filtered.CountAsync(cancellationToken);
        var items = await filtered.OrderByDescending(a => a.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(a => new AuditLogEntry(a.Id, a.OccurredAt, a.ActorUserName, a.Action, a.Detail, a.NodeName))
            .ToListAsync(cancellationToken);
        return new AuditLogPage(items, total, page, pageSize);
    }

    /// <summary>絞り込み用の選択肢（記録されている操作・実行者・ノード）。</summary>
    public async Task<AuditLogFacets> FacetsAsync(CancellationToken cancellationToken = default) => new(
        await db.AuditLogs.Select(a => a.Action).Distinct().OrderBy(a => a).ToListAsync(cancellationToken),
        await db.AuditLogs.Select(a => a.ActorUserName).Distinct().OrderBy(a => a).ToListAsync(cancellationToken),
        await db.AuditLogs.Where(a => a.NodeName != null).Select(a => a.NodeName!).Distinct().OrderBy(a => a).ToListAsync(cancellationToken));

    /// <summary>
    /// 条件に合う記録を新しい順にCSV（UTF-8 BOM付き。Excelで文字化けしないため）で書き出す。最大 MaxExportRows 件。
    /// </summary>
    public async Task ExportCsvAsync(AuditLogQuery query, Stream output, CancellationToken cancellationToken = default)
    {
        await using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true);
        await writer.WriteLineAsync("ID,日時,実行者,操作,ノード,内容");
        var rows = Filter(query).OrderByDescending(a => a.Id).Take(MaxExportRows).AsNoTracking().AsAsyncEnumerable();
        await foreach (var a in rows.WithCancellation(cancellationToken))
        {
            await writer.WriteLineAsync(string.Join(',',
                a.Id.ToString(CultureInfo.InvariantCulture),
                Csv(a.OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)),
                Csv(a.ActorUserName), Csv(a.Action), Csv(a.NodeName), Csv(a.Detail)));
        }
    }

    private IQueryable<AuditLogEntity> Filter(AuditLogQuery query)
    {
        var logs = db.AuditLogs.AsQueryable();
        if (query.From is { } from) logs = logs.Where(a => a.OccurredAt >= from);
        if (query.To is { } to) logs = logs.Where(a => a.OccurredAt < to);
        if (Trimmed(query.Actor) is { } actor) logs = logs.Where(a => a.ActorUserName == actor);
        // 操作は前方一致（"deploy" で deploy.* をまとめて絞り込める）
        if (Trimmed(query.Action) is { } action) logs = logs.Where(a => a.Action.StartsWith(action));
        if (Trimmed(query.Node) is { } node) logs = logs.Where(a => a.NodeName == node);
        if (Trimmed(query.Text) is { } text) logs = logs.Where(a => a.Detail != null && a.Detail.Contains(text));
        return logs;
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// CSVの1項目。表計算ソフトで式として解釈されないよう、= + - @ 等で始まる値の先頭に ' を付ける。
    /// </summary>
    public static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }
}
