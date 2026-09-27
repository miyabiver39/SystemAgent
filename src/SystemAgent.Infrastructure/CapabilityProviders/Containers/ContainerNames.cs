using System.Text.RegularExpressions;

namespace SystemAgent.Infrastructure.CapabilityProviders.Containers;

/// <summary>コンテナ・イメージ・タグの名前の形式（複数の機能で同じ規則を使うため、ここにまとめる）。</summary>
public static partial class ContainerNames
{
    /// <summary>イメージのタグ（Docker/OCIの規則）。</summary>
    public static bool IsValidTag(string tag) => TagPattern().IsMatch(tag);

    /// <summary>コンテナ名・Pod名として使える名前（英数字と _ . - 、63文字以内）。</summary>
    public static bool IsValidContainerName(string name) => ContainerNamePattern().IsMatch(name);

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]{0,62}$")]
    private static partial Regex ContainerNamePattern();
}
