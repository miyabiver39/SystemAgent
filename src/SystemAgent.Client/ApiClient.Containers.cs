using System.Net.Http.Json;
using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Core.Contracts;

namespace SystemAgent.Client;

// コンテナ・Pod・イメージ・レジストリ
public sealed partial class ApiClient
{
    public Task<HostEnvironment> GetEnvironmentAsync(bool refresh = false, CancellationToken cancellationToken = default) =>
        SendAsync<HostEnvironment>(HttpMethod.Get, $"api/system/environment?refresh={refresh}", null, authorize: true, cancellationToken);

    public Task<ContainerRuntimeResponse> GetContainerRuntimeAsync(CancellationToken cancellationToken = default) =>
        SendAsync<ContainerRuntimeResponse>(HttpMethod.Get, "api/containers/runtime", null, authorize: true, cancellationToken);

    public Task<List<ContainerInfo>> GetContainersAsync(CancellationToken cancellationToken = default) =>
        SendAsync<List<ContainerInfo>>(HttpMethod.Get, "api/containers", null, authorize: true, cancellationToken);

    /// <param name="action">start / stop / restart</param>
    public Task ContainerActionAsync(string id, string action, CancellationToken cancellationToken = default) =>
        SendAndDisposeAsync(HttpMethod.Post, $"api/containers/{Uri.EscapeDataString(id)}/{action}", null, authorize: true, cancellationToken);

    public Task RemoveContainerAsync(string id, CancellationToken cancellationToken = default) =>
        SendAndDisposeAsync(HttpMethod.Delete, $"api/containers/{Uri.EscapeDataString(id)}", null, authorize: true, cancellationToken);

    public Task<ContainerLogsResponse> GetContainerLogsAsync(string id, int tail, CancellationToken cancellationToken = default) =>
        SendAsync<ContainerLogsResponse>(HttpMethod.Get, $"api/containers/{Uri.EscapeDataString(id)}/logs?tail={tail}", null, authorize: true, cancellationToken);

    public Task<List<PodInfo>> GetPodsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<List<PodInfo>>(HttpMethod.Get, "api/pods", null, authorize: true, cancellationToken);

    public Task<List<ImageInfo>> GetImagesAsync(CancellationToken cancellationToken = default) =>
        SendAsync<List<ImageInfo>>(HttpMethod.Get, "api/images", null, authorize: true, cancellationToken);

    public Task PullImageAsync(string image, CancellationToken cancellationToken = default) =>
        SendAndDisposeAsync(HttpMethod.Post, "api/images/pull", new PullImageRequest(image), authorize: true, cancellationToken);

    public Task PushImageAsync(string image, string target, CancellationToken cancellationToken = default) =>
        SendAndDisposeAsync(HttpMethod.Post, "api/images/push", new PushImageRequest(image, target), authorize: true, cancellationToken);

    public Task RemoveImageAsync(string id, CancellationToken cancellationToken = default) =>
        SendAndDisposeAsync(HttpMethod.Delete, $"api/images/{Uri.EscapeDataString(id)}", null, authorize: true, cancellationToken);

    /// <summary>イメージアーカイブ(tar)をストリームのまま送信する。</summary>
    public async Task<ImportImageResponse> ImportImageAsync(Stream archive, string fileName, CancellationToken cancellationToken = default)
    {
        using var content = new MultipartFormDataContent { { new StreamContent(archive), "file", fileName } };
        using var response = await SendCoreAsync(HttpMethod.Post, "api/images/import", content, authorize: true, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<ImportImageResponse>(Json, cancellationToken))!;
    }

    public Task<List<RegistryView>> GetRegistriesAsync(CancellationToken cancellationToken = default) =>
        SendAsync<List<RegistryView>>(HttpMethod.Get, "api/registries", null, authorize: true, cancellationToken);

    public Task SaveRegistryAsync(SaveRegistryRequest request, CancellationToken cancellationToken = default) =>
        SendAndDisposeAsync(HttpMethod.Put, "api/registries", request, authorize: true, cancellationToken);

    public Task RemoveRegistryAsync(string registry, CancellationToken cancellationToken = default) =>
        SendAndDisposeAsync(HttpMethod.Delete, $"api/registries/{Uri.EscapeDataString(registry)}", null, authorize: true, cancellationToken);

    public Task<List<string>> GetRegistryRepositoriesAsync(string registry, CancellationToken cancellationToken = default) =>
        SendAsync<List<string>>(HttpMethod.Get, $"api/registries/{Uri.EscapeDataString(registry)}/repositories", null, authorize: true, cancellationToken);

    public Task<RegistryTagsResponse> GetRegistryTagsAsync(string registry, string repository, CancellationToken cancellationToken = default) =>
        SendAsync<RegistryTagsResponse>(HttpMethod.Get,
            $"api/registries/{Uri.EscapeDataString(registry)}/tags?repository={Uri.EscapeDataString(repository)}", null, authorize: true, cancellationToken);

    public Task DeleteRegistryTagAsync(string registry, string repository, string tag, CancellationToken cancellationToken = default) =>
        SendAndDisposeAsync(HttpMethod.Delete,
            $"api/registries/{Uri.EscapeDataString(registry)}/tags?repository={Uri.EscapeDataString(repository)}&tag={Uri.EscapeDataString(tag)}",
            null, authorize: true, cancellationToken);
}
