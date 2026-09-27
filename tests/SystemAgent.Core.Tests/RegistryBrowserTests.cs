using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Infrastructure.CapabilityProviders.Containers;
using SystemAgent.Infrastructure.Security;
using FakeRunner = SystemAgent.Core.Tests.CommandTemplateTests.FakeRunner;
using SystemAgent.Core.Errors;

namespace SystemAgent.Core.Tests;

public sealed class RegistryBrowserTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sa-browser-tests-" + Guid.NewGuid());
    private readonly List<HttpRequestMessage> _requests = [];
    private Func<HttpRequestMessage, HttpResponseMessage> _respond = _ => new HttpResponseMessage(HttpStatusCode.OK);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private async Task<RegistryBrowser> BrowserAsync(bool tlsVerify = true)
    {
        var registries = new RegistryService(
            new LocalSecretStore(Path.Combine(_dir, "secrets"), NullLogger<LocalSecretStore>.Instance), NullLogger<RegistryService>.Instance);
        var store = new SystemAgent.Infrastructure.CapabilityProviders.Templates.CommandTemplateStore(
            Path.Combine(AppContext.BaseDirectory, "CommandTemplates"), null, NullLogger<SystemAgent.Infrastructure.CapabilityProviders.Templates.CommandTemplateStore>.Instance);
        var runtime = new TemplateContainerRuntimeProvider(
            new SystemAgent.Infrastructure.CapabilityProviders.Templates.TemplateCommandExecutor(store.Templates.Single(t => t.Id == "podman"), new FakeRunner()),
            new RuntimeInfo("podman", "5", "podman"));
        await registries.SaveAsync(runtime, "zot.local:5000", "deploy", "pass", tlsVerify);
        return new RegistryBrowser(registries, _ => new StubHandler(request =>
        {
            _requests.Add(request);
            return _respond(request);
        }));
    }

    private static HttpResponseMessage Json(string json, string? link = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (link is not null) response.Headers.TryAddWithoutValidation("Link", link);
        return response;
    }

    [Fact]
    public async Task ListRepositories_FollowsLinkPagination()
    {
        var browser = await BrowserAsync();
        _respond = request => request.RequestUri!.Query.Contains("last=")
            ? Json("""{"repositories":["app/api"]}""")
            : Json("""{"repositories":["app/web","base/alpine"]}""", """</v2/_catalog?n=1000&last=base%2Falpine>; rel="next" """);

        var repositories = await browser.ListRepositoriesAsync("zot.local:5000");

        Assert.Equal(["app/api", "app/web", "base/alpine"], repositories);
        Assert.Equal("https://zot.local:5000/v2/_catalog?n=1000", _requests[0].RequestUri!.ToString());
        Assert.Equal(2, _requests.Count);
    }

    [Fact]
    public async Task ListTags_ReturnsSortedTags_AndHandlesNull()
    {
        var browser = await BrowserAsync();
        _respond = _ => Json("""{"name":"app/web","tags":["1.1","1.0"]}""");
        Assert.Equal(["1.0", "1.1"], await browser.ListTagsAsync("zot.local:5000", "app/web"));
        Assert.Equal("https://zot.local:5000/v2/app/web/tags/list?n=1000", _requests[^1].RequestUri!.ToString());

        _respond = _ => Json("""{"name":"app/web","tags":null}""");
        Assert.Empty(await browser.ListTagsAsync("zot.local:5000", "app/web"));
    }

    [Fact]
    public async Task DeleteTag_UsesTagReference_AndMapsErrors()
    {
        var browser = await BrowserAsync();
        _respond = _ => new HttpResponseMessage(HttpStatusCode.Accepted);
        await browser.DeleteTagAsync("zot.local:5000", "app/web", "1.0");
        Assert.Equal(HttpMethod.Delete, _requests[^1].Method);
        Assert.Equal("https://zot.local:5000/v2/app/web/manifests/1.0", _requests[^1].RequestUri!.ToString());

        _respond = _ => new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        var unsupported = await Assert.ThrowsAsync<RegistryRequestException>(() => browser.DeleteTagAsync("zot.local:5000", "app/web", "1.0"));
        Assert.Contains("対応していない", unsupported.Message);

        _respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var auth = await Assert.ThrowsAsync<RegistryRequestException>(() => browser.DeleteTagAsync("zot.local:5000", "app/web", "1.0"));
        Assert.Contains("認証に失敗", auth.Message);

        _respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        await Assert.ThrowsAsync<NotFoundException>(() => browser.DeleteTagAsync("zot.local:5000", "app/web", "1.0"));
    }

    [Theory]
    [InlineData("App/Web")]
    [InlineData("../etc")]
    [InlineData("app//web")]
    public async Task InvalidRepository_IsRejectedBeforeRequest(string repository)
    {
        var browser = await BrowserAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => browser.ListTagsAsync("zot.local:5000", repository));
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task TlsVerifyDisabled_FallsBackToHttp()
    {
        var browser = await BrowserAsync(tlsVerify: false);
        _respond = request => request.RequestUri!.Scheme == "https"
            ? throw new HttpRequestException("The SSL connection could not be established")
            : Json("""{"repositories":["app/web"]}""");

        Assert.Equal(["app/web"], await browser.ListRepositoriesAsync("zot.local:5000"));
        Assert.Equal("http://zot.local:5000/v2/_catalog?n=1000", _requests[^1].RequestUri!.ToString());
    }

    [Fact]
    public async Task TlsVerifyEnabled_DoesNotFallBackToHttp()
    {
        var browser = await BrowserAsync();
        _respond = _ => throw new HttpRequestException("The SSL connection could not be established");
        var ex = await Assert.ThrowsAsync<RegistryRequestException>(() => browser.ListRepositoriesAsync("zot.local:5000"));
        Assert.True(ex.Unreachable);
        Assert.Single(_requests);
    }

    [Fact]
    public async Task UnregisteredRegistry_IsNotFound()
    {
        var browser = await BrowserAsync();
        await Assert.ThrowsAsync<NotFoundException>(() => browser.ListRepositoriesAsync("other.local:5000"));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
