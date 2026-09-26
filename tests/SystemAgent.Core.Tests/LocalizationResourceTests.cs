using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SystemAgent.Core.Tests;

/// <summary>
/// 画面で使うリソースキーの欠落を検出する。IStringLocalizerはキーが無いとキー名をそのまま表示してしまうため。
/// </summary>
public partial class LocalizationResourceTests
{
    private static readonly string WebProjectDir = Path.Combine(FindRepositoryRoot(), "src", "SystemAgent.Web");

    // 実行時に組み立てるキー（L["Role_" + role] 等）
    private static readonly string[] DynamicKeys =
    [
        "AuthSource_db", "AuthSource_local",
        "Role_Master", "Role_Replica", "Role_Managed",
        .. Enum.GetNames<CapabilityProviders.ContainerState>().Select(s => "State_" + s),
    ];

    [Fact]
    public void AllKeysUsedInRazorFiles_ExistInSharedResource()
    {
        var defined = XDocument.Load(Path.Combine(WebProjectDir, "Resources", "SharedResource.resx"))
            .Root!.Elements("data").Select(e => (string)e.Attribute("name")!).ToHashSet();

        var used = Directory.EnumerateFiles(WebProjectDir, "*.razor", SearchOption.AllDirectories)
            .SelectMany(file => LocalizerKey().Matches(File.ReadAllText(file)).Select(m => m.Groups[1].Value))
            .Concat(DynamicKeys)
            .ToHashSet();

        Assert.Empty(used.Except(defined).Order());
    }

    [Fact]
    public void RoleKeys_CoverAllNodeRoles()
    {
        var roleKeys = DynamicKeys.Where(k => k.StartsWith("Role_")).Select(k => k["Role_".Length..]);
        Assert.Equal(Enum.GetNames<Nodes.NodeRole>().Order(), roleKeys.Order());
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SystemAgent.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("リポジトリルートが見つかりません");
    }

    [GeneratedRegex("""L\["([A-Za-z0-9_]+)"\]""")]
    private static partial Regex LocalizerKey();
}
