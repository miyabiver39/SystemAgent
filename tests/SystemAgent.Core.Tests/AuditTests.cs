using SystemAgent.Infrastructure.Auditing;

namespace SystemAgent.Core.Tests;

public class AuditTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("deploy.run", "deploy.run")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line1\nline2", "\"line1\nline2\"")]
    [InlineData("=HYPERLINK(\"http://x\")", "\"'=HYPERLINK(\"\"http://x\"\")\"")]
    [InlineData("+1", "'+1")]
    [InlineData("-1", "'-1")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    public void Csv_EscapesAndNeutralizesFormulas(string? value, string expected) =>
        Assert.Equal(expected, AuditLogReader.Csv(value));
}
