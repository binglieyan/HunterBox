namespace HunterBox.Tests;

/// <summary>
/// EditorConfig 求值器的专属单测：段解析、后段覆盖与 glob 语法分支。
/// </summary>
public class EditorConfigEvaluatorTests
{
    /// <summary>后匹配的段覆盖前值；空值不写入，与最终值求值语义一致。</summary>
    [Fact]
    public void Evaluate_LaterMatchingSection_OverridesEarlier()
    {
        string[] lines =
        [
            "[*]",
            "charset = utf-8-bom",
            "end_of_line = crlf",
            "empty =",
            "[*.md]",
            "end_of_line = lf",
        ];

        Dictionary<string, string> effective = EditorConfigEvaluator.Evaluate(string.Join("\n", lines), "CHANGELOG.md");
        Assert.Equal("utf-8-bom", effective["charset"]);
        Assert.Equal("lf", effective["end_of_line"]);

        Dictionary<string, string> section = EditorConfigEvaluator.ReadSection(lines, "[*]");
        Assert.Equal("utf-8-bom", section["charset"]);
        Assert.False(section.ContainsKey("empty"), "空值不应写入段字典");
    }

    /// <summary>`?` 匹配单个非分隔字符，`{a,b}` 匹配任一候选。</summary>
    [Fact]
    public void SectionMatches_SupportsQuestionAndAlternation()
    {
        Assert.True(EditorConfigEvaluator.SectionMatches("file?.md", "file1.md"));
        Assert.False(EditorConfigEvaluator.SectionMatches("file?.md", "file10.md"));
        Assert.True(EditorConfigEvaluator.SectionMatches("*.{md,txt}", "CHANGELOG.md"));
        Assert.True(EditorConfigEvaluator.SectionMatches("*.{md,txt}", "notes.txt"));
        Assert.False(EditorConfigEvaluator.SectionMatches("*.{md,txt}", "image.png"));
    }

    /// <summary>`**` 可跨目录，`[!...]` 为否定字符类。</summary>
    [Fact]
    public void SectionMatches_SupportsNegatedCharacterClassAndDoubleStar()
    {
        Assert.True(EditorConfigEvaluator.SectionMatches("src/**/*.cs", "src/HunterBox.App/MainWindow.cs"));
        Assert.True(EditorConfigEvaluator.SectionMatches("[!a]*.cs", "Model.cs"));
        Assert.False(EditorConfigEvaluator.SectionMatches("[!a]*.cs", "aModel.cs"));
    }
}
