using SystemAgent.Infrastructure.Commands;

namespace SystemAgent.Core.Tests;

public sealed class CommandArgumentsTests
{
    [Theory]
    [InlineData("--env=DB_PASSWORD=p@ss", "--env=DB_PASSWORD=***")]
    [InlineData("--env=API_TOKEN=abc", "--env=API_TOKEN=***")]
    [InlineData("--password=p@ss", "--password=***")]
    [InlineData("MYSQL_ROOT_PASSWORD=p@ss", "MYSQL_ROOT_PASSWORD=***")]
    [InlineData("--env=TZ=Asia/Tokyo", "--env=TZ=Asia/Tokyo")]
    [InlineData("--publish=8080:80", "--publish=8080:80")]
    [InlineData("--volume=/srv/keys:/keys:ro", "--volume=/srv/keys:/keys:ro")]
    [InlineData("--password-stdin", "--password-stdin")]
    [InlineData("--tls-verify=false", "--tls-verify=false")]
    public void Mask_HidesSecretValues(string argument, string expected) =>
        Assert.Equal(expected, Assert.Single(CommandArguments.Mask([argument])));

    [Fact]
    public void Mask_HidesValueFollowingSecretFlag()
    {
        Assert.Equal(["login", "--username", "admin", "--password", "***", "registry.local"],
            CommandArguments.Mask(["login", "--username", "admin", "--password", "p@ss", "registry.local"]));
        Assert.Equal(["login", "--password-stdin", "registry.local"],
            CommandArguments.Mask(["login", "--password-stdin", "registry.local"]));
    }

    [Fact]
    public void Describe_JoinsExecutableAndMaskedArguments() =>
        Assert.Equal("podman run --env=SECRET_KEY=*** app", CommandArguments.Describe("podman", ["run", "--env=SECRET_KEY=x", "app"]));
}
