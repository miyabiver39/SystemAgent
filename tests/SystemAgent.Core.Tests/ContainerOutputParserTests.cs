using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Infrastructure.CapabilityProviders.Containers;

namespace SystemAgent.Core.Tests;

public class ContainerOutputParserTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void Podman5_Containers_IncludesPodAndInfraFlag()
    {
        var containers = ContainerOutputParsers.Containers["podman-containers-json"](Fixture("podman5-ps.json"));

        Assert.Equal(3, containers.Count);
        var web = containers.Single(c => c.Name == "sa-fixture-web");
        Assert.Equal(ContainerState.Running, web.State);
        Assert.Equal("docker.io/library/alpine:latest", web.Image);
        Assert.Equal("sa-fixture-pod", web.Pod);
        Assert.False(web.IsInfra);
        Assert.Equal(64, web.Id.Length);
        Assert.NotNull(web.CreatedAt);

        Assert.True(containers.Single(c => c.Name.EndsWith("-infra")).IsInfra);

        var stopped = containers.Single(c => c.Name == "sa-fixture-stopped");
        Assert.Equal(ContainerState.Created, stopped.State);
        Assert.Null(stopped.Pod);
    }

    [Fact]
    public void Podman4_Containers_Parses()
    {
        var container = Assert.Single(ContainerOutputParsers.Containers["podman-containers-json"](Fixture("podman4-ps.json")));
        Assert.Equal("sa-fixture-p4", container.Name);
        Assert.Equal(ContainerState.Running, container.State);
    }

    [Theory]
    [InlineData("podman5-images.json")]
    [InlineData("podman4-images.json")]
    public void Podman_Images_UsesNamesAsTags(string fixture)
    {
        var image = Assert.Single(ContainerOutputParsers.Images["podman-images-json"](Fixture(fixture)));
        Assert.Equal(["docker.io/library/alpine:latest"], image.Tags);
        Assert.True(image.SizeBytes > 1_000_000);
        Assert.NotNull(image.CreatedAt);
    }

    [Fact]
    public void Podman_Pods_CountsContainers()
    {
        var pod = Assert.Single(ContainerOutputParsers.Pods["podman-pods-json"](Fixture("podman5-pods.json")));
        Assert.Equal("sa-fixture-pod", pod.Name);
        Assert.Equal("Running", pod.Status);
        Assert.Equal(2, pod.ContainerCount);
        Assert.NotNull(pod.CreatedAt);
    }

    [Fact]
    public void Docker_Containers_ParsesJsonLines()
    {
        var containers = ContainerOutputParsers.Containers["docker-containers-jsonl"](Fixture("docker-ps.jsonl"));

        Assert.Equal(2, containers.Count);
        var web = containers.Single(c => c.Name == "sa-fixture-web");
        Assert.Equal(ContainerState.Running, web.State);
        Assert.Equal("alpine", web.Image);
        Assert.Null(web.Pod);
        Assert.Equal(new DateTimeOffset(2026, 9, 26, 13, 48, 37, TimeSpan.FromHours(9)), web.CreatedAt);
        Assert.Equal(ContainerState.Created, containers.Single(c => c.Name == "sa-fixture-stopped").State);
    }

    [Fact]
    public void Docker_Containers_WithoutStateColumn_InfersFromStatus()
    {
        // Docker 20.10未満はState列が無い
        const string legacy = """
            {"ID":"a1","Names":"web","Image":"nginx","Status":"Up 2 hours","CreatedAt":"2020-01-01 00:00:00 +0000 UTC"}
            {"ID":"b2","Names":"job","Image":"busybox","Status":"Exited (0) 3 days ago","CreatedAt":"2020-01-01 00:00:00 +0000 UTC"}
            {"ID":"c3","Names":"paused","Image":"busybox","Status":"Up 1 minute (Paused)","CreatedAt":"2020-01-01 00:00:00 +0000 UTC"}
            """;
        var containers = ContainerOutputParsers.Containers["docker-containers-jsonl"](legacy);

        Assert.Equal([ContainerState.Running, ContainerState.Exited, ContainerState.Paused], containers.Select(c => c.State));
    }

    [Fact]
    public void Docker_Images_ParsesTagsAndSize()
    {
        var image = Assert.Single(ContainerOutputParsers.Images["docker-images-jsonl"](Fixture("docker-images.jsonl")));
        Assert.Equal(["alpine:latest"], image.Tags);
        Assert.Equal(13_000_000, image.SizeBytes);
        Assert.StartsWith("sha256:", image.Id);
    }

    [Fact]
    public void Docker_Images_UntaggedHasNoTags()
    {
        var image = Assert.Single(ContainerOutputParsers.Images["docker-images-jsonl"](
            """{"ID":"sha256:x","Repository":"<none>","Tag":"<none>","Size":"1.5GB","CreatedAt":"2020-01-01 00:00:00 +0000 UTC"}"""));
        Assert.Empty(image.Tags);
        Assert.Equal(1_500_000_000, image.SizeBytes);
    }

    [Fact]
    public void Images_SameIdWithSeveralTags_AreMerged()
    {
        // podman はタグごとに同じIDを複数行で返すことがある（画面の一覧でキーが重複しないよう1件にまとめる）
        var podman = Assert.Single(ContainerOutputParsers.Images["podman-images-json"](
            """[{"Id":"aaa","Names":["localhost/a:1","docker.io/library/busybox:latest"],"Size":10},{"Id":"aaa","Names":["localhost/a:1","docker.io/library/busybox:latest"],"Size":10}]"""));
        Assert.Equal(["localhost/a:1", "docker.io/library/busybox:latest"], podman.Tags);

        var docker = Assert.Single(ContainerOutputParsers.Images["docker-images-jsonl"](
            """
            {"ID":"sha256:x","Repository":"busybox","Tag":"latest","Size":"4MB","CreatedAt":"2020-01-01 00:00:00 +0000 UTC"}
            {"ID":"sha256:x","Repository":"localhost/a","Tag":"1","Size":"4MB","CreatedAt":"2020-01-01 00:00:00 +0000 UTC"}
            """));
        Assert.Equal(["busybox:latest", "localhost/a:1"], docker.Tags);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("[]", 0)]
    public void EmptyOutput_ReturnsEmpty(string output, int expected)
    {
        Assert.Equal(expected, ContainerOutputParsers.Containers["podman-containers-json"](output).Count);
        Assert.Equal(expected, ContainerOutputParsers.Containers["docker-containers-jsonl"](output == "[]" ? "" : output).Count);
    }

    [Theory]
    [InlineData("4.1kB (virtual 9.11MB)", 4100L)]
    [InlineData("512B", 512L)]
    [InlineData("N/A", null)]
    public void ParseHumanSize(string text, long? expected) => Assert.Equal(expected, ContainerOutputParsers.ParseHumanSize(text));

    [Fact]
    public void ParseDockerTimestamp_NegativeOffset() =>
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(-5)),
            ContainerOutputParsers.ParseDockerTimestamp("2026-01-02 03:04:05 -0500 EST"));
}
