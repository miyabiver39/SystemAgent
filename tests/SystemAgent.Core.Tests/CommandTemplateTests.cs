using Microsoft.Extensions.Logging.Abstractions;
using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Infrastructure.CapabilityProviders;
using SystemAgent.Infrastructure.CapabilityProviders.Containers;
using SystemAgent.Infrastructure.CapabilityProviders.Templates;
using SystemAgent.Infrastructure.Commands;

namespace SystemAgent.Core.Tests;

public sealed class CommandTemplateTests : IDisposable
{
    private static readonly string BuiltInDir = Path.Combine(AppContext.BaseDirectory, "CommandTemplates");
    private readonly string _extraDir = Path.Combine(Path.GetTempPath(), "sa-template-tests-" + Guid.NewGuid());

    public CommandTemplateTests() => Directory.CreateDirectory(_extraDir);

    public void Dispose() => Directory.Delete(_extraDir, recursive: true);

    private CommandTemplateStore Store() => new(BuiltInDir, _extraDir, NullLogger<CommandTemplateStore>.Instance);

    private static HostEnvironment Env(string osId, string[] idLike, params ToolInfo[] tools) =>
        new("host", osId, idLike, "9.8", osId, "x86_64", tools);

    [Fact]
    public void BuiltInTemplates_AreValid()
    {
        var store = Store();
        Assert.Contains(store.Templates, t => t.Id == "podman");
        Assert.Contains(store.Templates, t => t.Id == "docker");
    }

    [Fact]
    public void Resolve_PicksByToolAndVersion()
    {
        var store = Store();
        var env = Env("almalinux", ["rhel"], new ToolInfo("podman", "5.8.2"), new ToolInfo("docker", "1.12.0"));

        Assert.Equal("podman", store.Resolve("container-runtime", "podman", env)?.Id);
        Assert.Null(store.Resolve("container-runtime", "docker", env)); // 1.13未満
        Assert.Null(store.Resolve("container-runtime", "podman", Env("almalinux", [], new ToolInfo("docker", "29.1"))));
    }

    [Fact]
    public void Resolve_PrefersOsSpecificThenHigherMinVersion()
    {
        WriteExtra("podman-rhel.json", """
            { "id": "podman-rhel", "capability": "container-runtime", "executable": "podman",
              "match": { "tool": "podman", "osIds": ["rhel"], "minToolVersion": "4.0" }, "commands": {COMMANDS} }
            """);
        WriteExtra("podman-new.json", """
            { "id": "podman-new", "capability": "container-runtime", "executable": "podman",
              "match": { "tool": "podman", "minToolVersion": "5.0" }, "commands": {COMMANDS} }
            """);
        var store = Store();

        Assert.Equal("podman-rhel", store.Resolve("container-runtime", "podman", Env("almalinux", ["rhel", "centos"], new ToolInfo("podman", "5.8.2")))?.Id);
        Assert.Equal("podman-new", store.Resolve("container-runtime", "podman", Env("ubuntu", ["debian"], new ToolInfo("podman", "5.1.0")))?.Id);
        Assert.Equal("podman", store.Resolve("container-runtime", "podman", Env("ubuntu", ["debian"], new ToolInfo("podman", "4.9.3")))?.Id);
    }

    [Fact]
    public void ExtraTemplate_WithSameId_Overrides()
    {
        WriteExtra("podman.json", """
            { "id": "podman", "capability": "container-runtime", "executable": "/opt/podman/bin/podman",
              "match": { "tool": "podman" }, "commands": {COMMANDS} }
            """);
        Assert.Equal("/opt/podman/bin/podman", Store().Templates.Single(t => t.Id == "podman").Executable);
    }

    [Fact]
    public void InvalidExtraTemplate_IsSkipped()
    {
        WriteExtra("broken.json", """{ "id": "broken", "capability": "container-runtime", "executable": "x", "match": { "tool": "x" }, "commands": {} }""");
        WriteExtra("not-json.json", "{ this is not json");
        Assert.DoesNotContain(Store().Templates, t => t.Id == "broken");
    }

    [Fact]
    public void Validate_ReportsMissingCommandsBadPlaceholdersAndParsers()
    {
        var template = new CommandTemplate
        {
            Id = "t",
            Capability = "container-runtime",
            Executable = "x",
            Match = new TemplateMatch { Tool = "x" },
            Commands = new()
            {
                ["listContainers"] = new CommandDefinition { Args = ["ps"], Parser = "no-such-parser" },
                ["startContainer"] = new CommandDefinition { Args = ["start", "{image}"] },
                ["unknownCommand"] = new CommandDefinition { Args = ["x"] },
            },
        };

        var errors = TemplateRequirements.Validate(template);

        Assert.Contains(errors, e => e.Contains("'stopContainer'"));
        Assert.Contains(errors, e => e.Contains("{image}"));
        Assert.Contains(errors, e => e.Contains("parser"));
        Assert.Contains(errors, e => e.Contains("'unknownCommand'"));
    }

    [Fact]
    public async Task Provider_RendersArgsFromTemplate()
    {
        var runner = new FakeRunner();
        var provider = new TemplateContainerRuntimeProvider(
            new TemplateCommandExecutor(Store().Templates.Single(t => t.Id == "docker"), runner), new RuntimeInfo("docker", "29.1", "docker"));

        await provider.StopContainerAsync("web-1");
        await provider.GetContainerLogsAsync("web-1", 50);
        await provider.PullImageAsync("registry.local:5000/app/web:1.2.3");

        Assert.Equal(["docker stop web-1", "docker logs --tail 50 web-1", "docker pull registry.local:5000/app/web:1.2.3"], runner.Calls);
        Assert.False(provider.SupportsPods);
    }

    [Theory]
    [InlineData("--privileged")]
    [InlineData("web;rm -rf /")]
    [InlineData("")]
    [InlineData("a b")]
    public async Task Provider_RejectsUnsafeReferences(string reference)
    {
        var runner = new FakeRunner();
        var provider = new TemplateContainerRuntimeProvider(
            new TemplateCommandExecutor(Store().Templates.Single(t => t.Id == "podman"), runner), new RuntimeInfo("podman", "5.8", "podman"));

        await Assert.ThrowsAsync<ArgumentException>(() => provider.StartContainerAsync(reference));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Provider_ThrowsCommandFailed_WithStderr()
    {
        var runner = new FakeRunner { Result = new CommandResult(125, "", "Error: no such container") };
        var provider = new TemplateContainerRuntimeProvider(
            new TemplateCommandExecutor(Store().Templates.Single(t => t.Id == "podman"), runner), new RuntimeInfo("podman", "5.8", "podman"));

        var ex = await Assert.ThrowsAsync<CommandFailedException>(() => provider.StopContainerAsync("nope"));
        Assert.Equal(125, ex.ExitCode);
        Assert.Contains("no such container", ex.Message);
    }

    [Fact]
    public void ParseOsRelease_HandlesQuotesAndComments()
    {
        var values = EnvironmentDetector.ParseOsRelease(["# comment", "ID=\"almalinux\"", "ID_LIKE='rhel centos fedora'", "VERSION_ID=9.8", ""]);
        Assert.Equal("almalinux", values["ID"]);
        Assert.Equal("rhel centos fedora", values["ID_LIKE"]);
        Assert.Equal("9.8", values["VERSION_ID"]);
    }

    [Theory]
    [InlineData("podman version 5.8.2", "5.8.2")]
    [InlineData("Docker version 29.1.3, build 29.1.3-0ubuntu3", "29.1.3")]
    [InlineData("systemd 252 (252-67.el9_8.2.alma.1)", "252.0")]
    [InlineData("Keepalived v2.2.8 (04/04,2023)", "2.2.8")]
    [InlineData("no digits", "0.0")]
    public void VersionText_Parse(string text, string expected) => Assert.Equal(Version.Parse(expected), VersionText.Parse(text));

    private void WriteExtra(string name, string json)
    {
        // 必須コマンド一式は同梱podmanテンプレートから流用する
        var commands = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(BuiltInDir, "container-runtime", "podman.json")))
            .RootElement.GetProperty("commands").GetRawText();
        File.WriteAllText(Path.Combine(_extraDir, name), json.Replace("{COMMANDS}", commands));
    }

    internal sealed class FakeRunner : ICommandRunner
    {
        public List<string> Calls { get; } = [];
        public CommandResult Result { get; init; } = new(0, "", "");
        public Func<string, CommandResult?>? Respond { get; init; }

        public Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var call = string.Join(' ', [executable, .. arguments]);
            Calls.Add(call);
            return Task.FromResult(Respond?.Invoke(call) ?? Result);
        }

        /// <summary>ストリーミング実行で標準出力に書く内容。</summary>
        public byte[] StreamOutput { get; init; } = [];

        /// <summary>ストリーミング実行で標準入力から受け取った内容。</summary>
        public byte[] ReceivedInput { get; private set; } = [];

        public async Task<CommandResult> RunStreamingAsync(string executable, IReadOnlyList<string> arguments, Stream? stdin, Stream? stdout,
            TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var call = string.Join(' ', [executable, .. arguments]);
            Calls.Add(call);
            if (stdin is not null)
            {
                using var buffer = new MemoryStream();
                await stdin.CopyToAsync(buffer, cancellationToken);
                ReceivedInput = buffer.ToArray();
            }
            if (stdout is not null) await stdout.WriteAsync(StreamOutput, cancellationToken);
            return Respond?.Invoke(call) ?? Result;
        }
    }
}
