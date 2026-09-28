using SystemAgent.Infrastructure.Cluster;

namespace SystemAgent.Core.Tests;

public sealed class JoinTokenTests
{
    [Theory]
    [InlineData("https://10.0.0.11:5443")]
    [InlineData("https://[fd00::11]:5443")]
    [InlineData("https://ca.example.local:5443/")]
    public void Decode_AcceptsHttpsHostAndPort(string caUrl)
    {
        var token = JoinToken.Decode(new JoinToken(caUrl, "AB", "secret").Encode());

        Assert.Equal(caUrl.TrimEnd('/'), token.CaUrl);
        Assert.Equal(("AB", "secret"), (token.CaFingerprint, token.Secret));
    }

    [Theory]
    [InlineData("http://10.0.0.11:5443")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://10.0.0.11:5443/api/other?x=1")]
    [InlineData("https://user:pass@10.0.0.11:5443")]
    [InlineData("https://10.0.0.11:5443#frag")]
    [InlineData("10.0.0.11:5443")]
    [InlineData("")]
    public void Decode_RejectsOtherUrls(string caUrl) =>
        Assert.Throws<ArgumentException>(() => JoinToken.Decode(new JoinToken(caUrl, "AB", "secret").Encode()));

    [Fact]
    public void Decode_RejectsMalformedToken() =>
        Assert.Throws<ArgumentException>(() => JoinToken.Decode("SAJ1.not-base64!"));
}
