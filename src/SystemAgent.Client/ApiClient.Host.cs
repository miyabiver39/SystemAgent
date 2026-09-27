using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Core.Contracts;

namespace SystemAgent.Client;

// サービス・ネットワーク・時刻同期・監査ログ
public sealed partial class ApiClient
{
    public Task<List<ManagedServiceResponse>> GetServicesAsync(CancellationToken cancellationToken = default) =>
        SendAsync<List<ManagedServiceResponse>>(HttpMethod.Get, "api/services", null, authorize: true, cancellationToken);

    public Task<ManagedServiceResponse> GetServiceAsync(string unit, CancellationToken cancellationToken = default) =>
        SendAsync<ManagedServiceResponse>(HttpMethod.Get, $"api/services/{Uri.EscapeDataString(unit)}", null, authorize: true, cancellationToken);

    public Task ServiceActionAsync(string unit, ServiceAction action, CancellationToken cancellationToken = default) =>
        SendAndDisposeAsync(HttpMethod.Post, $"api/services/{Uri.EscapeDataString(unit)}/{action}", null, authorize: true, cancellationToken);

    public Task<ServiceLogsResponse> GetServiceLogsAsync(string unit, int lines, CancellationToken cancellationToken = default) =>
        SendAsync<ServiceLogsResponse>(HttpMethod.Get, $"api/services/{Uri.EscapeDataString(unit)}/logs?lines={lines}", null, authorize: true, cancellationToken);

    public Task<NetworkStatus> GetNetworkAsync(CancellationToken cancellationToken = default) =>
        SendAsync<NetworkStatus>(HttpMethod.Get, "api/network", null, authorize: true, cancellationToken);

    public Task<NtpResponse> GetNtpAsync(CancellationToken cancellationToken = default) =>
        SendAsync<NtpResponse>(HttpMethod.Get, "api/ntp", null, authorize: true, cancellationToken);

    public Task SetNtpServersAsync(IReadOnlyList<string> servers, CancellationToken cancellationToken = default) =>
        SendAndDisposeAsync(HttpMethod.Put, "api/ntp/servers", new SetNtpServersRequest(servers), authorize: true, cancellationToken);

    public Task SyncNtpAsync(CancellationToken cancellationToken = default) =>
        SendAndDisposeAsync(HttpMethod.Post, "api/ntp/sync", null, authorize: true, cancellationToken);

    public Task<AuditLogPage> GetAuditLogsAsync(AuditLogQuery query, CancellationToken cancellationToken = default) =>
        SendAsync<AuditLogPage>(HttpMethod.Get, $"api/audit?{AuditQueryString(query)}", null, authorize: true, cancellationToken);

    public Task<AuditLogFacets> GetAuditFacetsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<AuditLogFacets>(HttpMethod.Get, "api/audit/facets", null, authorize: true, cancellationToken);

    /// <summary>条件に合う監査ログをCSVで受け取る（ストリームのまま）。</summary>
    public async Task<Stream> ExportAuditCsvAsync(AuditLogQuery query, CancellationToken cancellationToken = default)
    {
        var response = await SendCoreAsync(HttpMethod.Get, $"api/audit/export?{AuditQueryString(query)}", null, authorize: true, cancellationToken,
            HttpCompletionOption.ResponseHeadersRead);
        return new ResponseStream(response, await response.Content.ReadAsStreamAsync(cancellationToken));
    }

    private static string AuditQueryString(AuditLogQuery q)
    {
        var parts = new List<string> { $"page={q.Page}", $"pageSize={q.PageSize}" };
        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) parts.Add($"{key}={Uri.EscapeDataString(value.Trim())}");
        }
        Add("from", q.From?.ToString("o"));
        Add("to", q.To?.ToString("o"));
        Add("actor", q.Actor);
        Add("action", q.Action);
        Add("node", q.Node);
        Add("text", q.Text);
        return string.Join('&', parts);
    }
}
