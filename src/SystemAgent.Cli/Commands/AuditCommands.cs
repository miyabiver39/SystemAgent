using System.CommandLine;
using SystemAgent.Client;
using SystemAgent.Core.Contracts;

namespace SystemAgent.Cli.Commands;

/// <summary>監査ログ（audit）</summary>
internal static class AuditCommands
{
    public static void Register(RootCommand root, CliContext cli)
    {
        var jsonOption = cli.JsonOption;
        Task<int> RunOnNode(ParseResult p, Func<ApiClient, Task> action) => cli.RunOnNode(p, action);

        var audit = new Command("audit", "監査ログ（全ノードの操作記録。中央DB）");
        var auditSince = new Option<DateOnly?>("--since") { Description = "開始日（例: 2026-09-01）" };
        var auditUntil = new Option<DateOnly?>("--until") { Description = "終了日（この日を含む）" };
        var auditUser = new Option<string?>("--user") { Description = "実行者" };
        var auditAction = new Option<string?>("--action") { Description = "操作（前方一致。例: deploy）" };
        var auditNode = new Option<string?>("--node-name") { Description = "操作を実行したノード" };
        var auditGrep = new Option<string?>("--grep") { Description = "内容に含む文字" };
        Option[] auditFilters = [auditSince, auditUntil, auditUser, auditAction, auditNode, auditGrep];
        AuditLogQuery AuditQuery(ParseResult p, int page, int pageSize)
        {
            static DateTimeOffset? Local(DateOnly? date) =>
                date?.ToDateTime(TimeOnly.MinValue) is { } d ? new DateTimeOffset(d, TimeZoneInfo.Local.GetUtcOffset(d)) : null;
            return new AuditLogQuery(Local(p.GetValue(auditSince)), Local(p.GetValue(auditUntil)?.AddDays(1)),
                p.GetValue(auditUser), p.GetValue(auditAction), p.GetValue(auditNode), p.GetValue(auditGrep), page, pageSize);
        }
        var auditLimit = new Option<int>("--limit", "-n") { Description = "表示件数（最大500）", DefaultValueFactory = _ => 50 };
        var auditPage = new Option<int>("--page") { Description = "ページ（1が最新）", DefaultValueFactory = _ => 1 };
        var auditList = new Command("list", "監査ログを新しい順に表示する");
        foreach (var option in auditFilters.Append(auditLimit).Append(auditPage)) auditList.Options.Add(option);
        auditList.SetAction((p, ct) => RunOnNode(p, async api =>
        {
            var result = await api.GetAuditLogsAsync(AuditQuery(p, p.GetValue(auditPage), p.GetValue(auditLimit)), ct);
            if (p.GetValue(jsonOption)) { ConsoleUi.WriteJson(result); return; }
            if (result.TotalCount == 0) { Console.WriteLine("条件に合う記録はありません。"); return; }
            ConsoleUi.WriteTable(["日時", "実行者", "操作", "ノード", "内容"],
                result.Items.Select(e => (IReadOnlyList<string>)
                [
                    e.OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), e.Actor, e.Action, e.NodeName ?? "-",
                    (e.Detail ?? "").ReplaceLineEndings(" "),
                ]));
            var first = (result.Page - 1) * result.PageSize + 1;
            Console.WriteLine($"{result.TotalCount} 件中 {first}〜{first + result.Items.Count - 1} 件目");
        }));
        audit.Subcommands.Add(auditList);
        var auditOut = new Option<FileInfo>("--output", "-o") { Description = "保存先のCSVファイル", Required = true };
        var auditExport = new Command("export", "条件に合う監査ログをCSVで保存する（最大10万件）");
        foreach (var option in auditFilters.Append(auditOut)) auditExport.Options.Add(option);
        auditExport.SetAction((p, ct) => RunOnNode(p, async api =>
        {
            var file = p.GetValue(auditOut)!;
            await using (var source = await api.ExportAuditCsvAsync(AuditQuery(p, 1, 1), ct))
            await using (var target = file.Create())
            {
                await source.CopyToAsync(target, ct);
            }
            Console.WriteLine($"{file.FullName} に保存しました。");
        }));
        audit.Subcommands.Add(auditExport);
        root.Subcommands.Add(audit);
    }
}
