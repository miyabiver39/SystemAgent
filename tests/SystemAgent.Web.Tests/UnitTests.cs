using System.Net;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using SystemAgent.Client;
using SystemAgent.Core.Errors;
using SystemAgent.Web.Api;
using SystemAgent.Web.Auth;
using SystemAgent.Web.Client;
using SystemAgent.Web.Controllers;

namespace SystemAgent.Web.Tests;

public sealed class PathRuleTests
{
    [Theory]
    [InlineData("api/containers", true)]
    [InlineData("api/images/registry.local%2Fapp%3A1", true)]
    [InlineData("api/nodes", true)]
    [InlineData("api/nodes/x/proxy/api/containers", false)]
    [InlineData("api/Nodes/x/proxy/api/containers", false)]
    [InlineData("API/NODES/x", false)]
    [InlineData("api%2Fnodes%2Fx", false)]
    [InlineData("api/containers/../nodes/x", false)]
    [InlineData("api/containers/%2e%2e/nodes/x", false)]
    [InlineData("api/./containers", false)]
    [InlineData("other", false)]
    public void NodeProxy_IsForwardablePath(string path, bool expected) =>
        Assert.Equal(expected, NodeProxyController.IsForwardablePath(path));

    [Theory]
    [InlineData("admin@node-a", true)]
    [InlineData("admin", true)]
    [InlineData("svc.deploy_1@node-a.example.local", true)]
    [InlineData("admin\r\nforged", false)]
    [InlineData("=cmd|' /C calc'!A0", false)]
    [InlineData("<script>@node", false)]
    [InlineData("", false)]
    public void NodeCertificate_IsValidActor(string actor, bool expected) =>
        Assert.Equal(expected, NodeCertificateAuthenticationHandler.IsValidActor(actor));

    [Theory]
    [InlineData("GET", "/api/cluster/ca", true)]
    [InlineData("POST", "/api/cluster/enroll", true)]
    [InlineData("POST", "/api/cluster/ca", false)]
    [InlineData("POST", "/api/auth/login", false)]
    [InlineData("GET", "/api/health", false)]
    [InlineData("GET", "/", false)]
    public void ClusterPort_AllowedWithoutCertificate(string method, string path, bool expected) =>
        Assert.Equal(expected, ClusterPortGuard.IsAllowedWithoutCertificate(method, path));

    [Theory]
    [InlineData("POST", "/api/containers/x/stop", true)]
    [InlineData("DELETE", "/api/users/bob", true)]
    [InlineData("PUT", "/api/ha", true)]
    [InlineData("GET", "/api/users", false)]
    [InlineData("POST", "/api/auth/login", false)]
    [InlineData("POST", "/api/ha/notify", false)]
    [InlineData("POST", "/_blazor/negotiate", false)]
    public void Maintenance_IsGuarded(string method, string path, bool expected) =>
        Assert.Equal(expected, MaintenanceGuard.IsGuarded(method, path));

    [Theory]
    [InlineData("admin", "admin")]
    [InlineData("bad\nname", "(不正なユーザー名)")]
    public void AuditActor_ReplacesInvalidUserNames(string input, string expected) =>
        Assert.Equal(expected, AuthController.AuditActor(input));
}

public sealed class LoginThrottleTests
{
    private readonly FakeTimeProvider _time = new();

    private LoginThrottle Throttle() => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Auth:LockoutThreshold"] = "3",
        ["Auth:LockoutMinutes"] = "10",
    }).Build(), _time);

    [Fact]
    public void LocksAfterThreshold_AndUnlocksAfterDuration()
    {
        var throttle = Throttle();
        Assert.False(throttle.RecordFailure("local:admin"));
        Assert.False(throttle.RecordFailure("local:admin"));
        Assert.True(throttle.RecordFailure("local:admin"));
        Assert.NotNull(throttle.LockedFor("local:admin"));
        Assert.Null(throttle.LockedFor("local:other"));

        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.Null(throttle.LockedFor("local:admin"));
    }

    [Fact]
    public void SuccessResetsFailures_AndOldFailuresExpire()
    {
        var throttle = Throttle();
        throttle.RecordFailure("db:admin");
        throttle.RecordFailure("db:admin");
        throttle.RecordSuccess("db:admin");
        Assert.False(throttle.RecordFailure("db:admin"));

        _time.Advance(TimeSpan.FromMinutes(11));
        throttle.RecordFailure("db:admin");
        Assert.False(throttle.RecordFailure("db:admin"));
    }
}

public sealed class ApiExceptionHandlerTests
{
    private static ApiExceptionHandler Handler() =>
        new(new NullProblemDetailsService(), NullLogger<ApiExceptionHandler>.Instance);

    private static DefaultHttpContext Context(bool started = false)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/test";
        if (started) context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
        return context;
    }

    [Theory]
    [InlineData(ErrorKind.InvalidInput, 400)]
    [InlineData(ErrorKind.NotFound, 404)]
    [InlineData(ErrorKind.Conflict, 409)]
    [InlineData(ErrorKind.TooLarge, 413)]
    [InlineData(ErrorKind.OperationFailed, 422)]
    [InlineData(ErrorKind.NotSupported, 501)]
    [InlineData(ErrorKind.Unreachable, 502)]
    [InlineData(ErrorKind.InsufficientStorage, 507)]
    public async Task MapsErrorKindToStatus(ErrorKind kind, int status)
    {
        var context = Context();
        Assert.True(await Handler().TryHandleAsync(context, new NodeForwardException(kind, "x"), CancellationToken.None));
        Assert.Equal(status, context.Response.StatusCode);
    }

    [Fact]
    public async Task ResponseAlreadyStarted_IsLeftToPipeline()
    {
        var context = Context(started: true);
        Assert.False(await Handler().TryHandleAsync(context, new InvalidOperationException("stream broke"), CancellationToken.None));
    }

    [Fact]
    public async Task NonApiPath_IsNotHandled()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/containers";
        Assert.False(await Handler().TryHandleAsync(context, new Exception(), CancellationToken.None));
    }

    private sealed class NullProblemDetailsService : IProblemDetailsService
    {
        public ValueTask WriteAsync(ProblemDetailsContext context) => ValueTask.CompletedTask;
        public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context) => ValueTask.FromResult(true);
    }

    private sealed class StartedResponseFeature : IHttpResponseFeature
    {
        public bool HasStarted => true;

        public int StatusCode
        {
            get => 200;
            set => throw new InvalidOperationException("ヘッダ送信後に変更しようとしました。");
        }

        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = Stream.Null;
        public void OnStarting(Func<object, Task> callback, object state) { }
        public void OnCompleted(Func<object, Task> callback, object state) { }
    }
}

public sealed class PageOperationTests
{
    [Fact]
    public async Task SecondRunWhileBusy_IsIgnored()
    {
        var op = new PageOperation();
        var gate = new TaskCompletionSource();
        var runs = 0;

        var first = op.RunAsync(async () => { runs++; await gate.Task; });
        await op.RunAsync(() => { runs++; return Task.CompletedTask; });
        gate.SetResult();
        await first;

        Assert.Equal(1, runs);
        Assert.False(op.Busy);
    }

    [Fact]
    public async Task ConnectionAndTimeoutFailures_AreShownAsErrors()
    {
        var op = new PageOperation();
        await op.RunAsync(() => throw new HttpRequestException("refused"));
        Assert.Equal(PageOperation.ConnectionFailedMessage, op.Error);

        await op.RunAsync(() => throw new TaskCanceledException());
        Assert.Equal(PageOperation.TimeoutMessage, op.Error);

        await op.RunAsync(() => throw new ApiException(HttpStatusCode.Conflict, "デプロイ中です"));
        Assert.Equal("デプロイ中です", op.Error);
        Assert.False(op.Busy);
    }

    [Fact]
    public async Task ReloadFailure_DoesNotHideMessage()
    {
        var op = new PageOperation();
        await op.RunAsync(() => { op.Message = "done"; return Task.CompletedTask; }, () => throw new HttpRequestException());
        Assert.Equal("done", op.Message);
        Assert.Equal(PageOperation.ConnectionFailedMessage, op.Error);
    }
}

public sealed class NodeSelectionTests
{
    [Fact]
    public async Task SelectAsync_WaitsForAllHandlers()
    {
        var selection = new NodeSelection();
        var completed = 0;
        for (var i = 0; i < 3; i++)
        {
            var delay = 50 * (3 - i); // 先に登録したものほど遅い
            selection.Changed += async () =>
            {
                await Task.Delay(delay);
                Interlocked.Increment(ref completed);
            };
        }

        await selection.SelectAsync(Guid.NewGuid(), "node-b");

        Assert.Equal(3, completed);
    }
}
