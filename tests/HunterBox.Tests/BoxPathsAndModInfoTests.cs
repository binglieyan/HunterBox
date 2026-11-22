using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;

namespace HunterBox.Tests;

public class BoxPathsTests
{
    [Fact]
    public void Layout_MatchesSpec()
    {
        using TempDir temp = TempDir.Create();
        string root = temp.Path;
        BoxPaths paths = new(root);

        Assert.Equal(Path.Combine(root, "Mods"), paths.ModsDirectory);
        Assert.Equal(Path.Combine(root, "Mods", "7"), paths.ModDirectory(7));
        Assert.Equal(Path.Combine(root, "Mods", "7", "files"), paths.FilesDirectory(7));
        Assert.Equal(Path.Combine(root, "Mods", ".backup"), paths.BackupRootDirectory);
        Assert.Equal(Path.Combine(root, "Mods", ".backup", "mhws"), paths.BackupDirectory("mhws"));
        Assert.Equal(Path.Combine(root, "Mods", ".backup", "mhw"), paths.BackupDirectory("mhw"));
        Assert.Equal(Path.Combine(root, "hunterbox.ini"), paths.SettingsFile);
    }

    [Fact]
    public void BackupFile_IsScopedByGameAndUsesForwardSlashRelativePath()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);

        string backup = paths.BackupFile("mhws", @"natives\stm\a.tex");
        string otherGame = paths.BackupFile("mhw", "natives/stm/a.tex");

        Assert.Equal(
            Path.Combine(paths.BackupRootDirectory, "mhws", "natives", "stm", "a.tex"),
            backup);
        Assert.Equal(
            Path.Combine(paths.BackupRootDirectory, "mhw", "natives", "stm", "a.tex"),
            otherGame);
        Assert.NotEqual(backup, otherGame);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public void BackupFile_RejectsUnsafeGameId(string gameId)
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);

        Assert.ThrowsAny<ArgumentException>(() => paths.BackupFile(gameId, "natives/stm/a.tex"));
    }

    [Theory]
    [InlineData("../escape.tex")]
    [InlineData("natives/../../escape.tex")]
    [InlineData("/abs.tex")]
    [InlineData("C:/windows/x.tex")]
    public void ResolveInside_RejectsEscapingPaths(string relative)
    {
        using TempDir temp = TempDir.Create();
        Assert.ThrowsAny<ArgumentException>(() => BoxPaths.ResolveInside(temp.Path, relative));
    }

    [Theory]
    [InlineData("natives/CON.tex")]
    [InlineData("natives/nul.dat")]
    [InlineData("nativepc/plugins/com1/x.dll")]
    [InlineData("natives/lpt9")]
    [InlineData("natives/a:b.tex")]
    [InlineData("natives/trailing.")]
    [InlineData("natives/trailing ")]
    public void NormalizeRelative_RejectsWindowsReservedAndInvalidSegments(string relative)
    {
        Assert.ThrowsAny<ArgumentException>(() => BoxPaths.NormalizeRelative(relative));
    }

    [Fact]
    public void NormalizeRelative_AllowsNormalReservedSubstrings()
    {
        Assert.Equal(
            "natives/console/controller.tex",
            BoxPaths.NormalizeRelative(@"natives\console\controller.tex"));
    }

    [Fact]
    public void ResolveInside_ReturnsPathUnderRoot()
    {
        using TempDir temp = TempDir.Create();
        string full = BoxPaths.ResolveInside(temp.Path, "natives/stm/a.tex");

        Assert.StartsWith(Path.GetFullPath(temp.Path), full, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("natives", "stm", "a.tex"), full, StringComparison.OrdinalIgnoreCase);
    }
}

public class ModInfoParserTests
{
    [Fact]
    public void ParsesBasicFields()
    {
        const string ini = """
            name=太刀特效
            category=特效
            version=1.2.0
            author=某人
            nexusid=123456
            homepage=https://www.nexusmods.com/monsterhunterwilds/mods/123456
            description=替换太刀特效
            """;

        ModMetadata meta = ModInfoParser.Parse(ini);

        Assert.Equal("太刀特效", meta.Name);
        Assert.Equal("特效", meta.Category);
        Assert.Equal("1.2.0", meta.Version);
        Assert.Equal("某人", meta.Author);
        Assert.Equal(123456, meta.NexusId);
        Assert.Equal("https://www.nexusmods.com/monsterhunterwilds/mods/123456", meta.HomeUrl);
        Assert.Equal("替换太刀特效", meta.Description);
    }

    [Fact]
    public void IgnoresCommentsSectionsAndBlankLines()
    {
        const string ini = """
            ; 注释
            # 另一个注释
            [ModInfo]

            name = 有空格的值

            """;

        ModMetadata meta = ModInfoParser.Parse(ini);

        Assert.Equal("有空格的值", meta.Name);
    }

    [Fact]
    public void KeyMatchingIsCaseInsensitive()
    {
        ModMetadata meta = ModInfoParser.Parse("NAME=abc\nVersion=9\nAUTHOR=me");

        Assert.Equal("abc", meta.Name);
        Assert.Equal("9", meta.Version);
        Assert.Equal("me", meta.Author);
    }

    [Fact]
    public void StripsQuotes()
    {
        Assert.Equal("quoted", ModInfoParser.Parse("name=\"quoted\"").Name);
        Assert.Equal("single", ModInfoParser.Parse("name='single'").Name);
    }

    [Fact]
    public void MissingFieldsAreNull()
    {
        ModMetadata meta = ModInfoParser.Parse("name=only");

        Assert.Equal("only", meta.Name);
        Assert.Null(meta.Version);
        Assert.Null(meta.NexusId);
    }

    [Fact]
    public void EmptyContentGivesEmptyMetadata()
    {
        ModMetadata meta = ModInfoParser.Parse(null);

        Assert.Null(meta.Name);
        Assert.Null(meta.NexusId);
    }

    [Theory]
    [InlineData("mods/12345", 12345)]
    [InlineData("https://www.nexusmods.com/monsterhunterwilds/mods/998877?tab=files", 998877)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    public void NexusIdAcceptsPlainNumberOrUrl(string raw, int? expected)
    {
        ModMetadata meta = ModInfoParser.Parse("nexusid=" + raw);

        Assert.Equal(expected, meta.NexusId);
    }
}
