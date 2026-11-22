using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HunterBox.Tests;

/// <summary>测试源码门禁的共享索引：特性名、特性短名与测试语法树。</summary>
internal static class TestSourceIndex
{
    /// <summary>本项目派生的事实特性短名（不含 <c>Fact</c>/<c>Theory</c>）；计数与门禁配置共用这一份。</summary>
    public static readonly string[] DerivedAttributeNames =
    [
        "SourceTreeFact", "FileSymlinkFact", "DirectoryLinkFact", "AclFact", "FileAclFact",
        "DirectoryAttributeAclFact", "LongPathFact", "SourceTreeDirectoryLinkFact",
    ];

    /// <summary>被视为测试方法的特性短名（含本项目的派生事实特性）。</summary>
    public static readonly string[] TestAttributeNames = ["Fact", "Theory", .. DerivedAttributeNames];

    /// <summary>测试项目全部语法树：由共享缓存解析一次，多个门禁复用。</summary>
    public static IReadOnlyList<Microsoft.CodeAnalysis.SyntaxTree> Trees => SourceTreeCompilationCache.TestTrees;

    /// <summary>取特性名的最后一段（忽略命名空间与 Attribute 后缀）。</summary>
    /// <param name="attribute">特性语法。</param>
    /// <returns>特性短名。</returns>
    public static string AttributeLastName(AttributeSyntax attribute)
    {
        string name = attribute.Name.ToString();
        if (name.EndsWith("Attribute", StringComparison.Ordinal))
        {
            name = name[..^"Attribute".Length];
        }

        int dot = name.LastIndexOf('.');
        return dot >= 0 ? name[(dot + 1)..] : name;
    }
}
