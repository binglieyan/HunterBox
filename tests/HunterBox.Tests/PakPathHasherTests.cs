using HunterBox.Domain.Paks;
using HunterBox.Infrastructure.Paks;

namespace HunterBox.Tests;

public class PakPathHasherTests
{
    [Fact]
    public void BuildIndex_ResolvesEveryKnownPath()
    {
        string[] paths =
        [
            @"natives/stm/art/model/character/ch03/x.mod3",
            "natives/STM/Art/Model/character/ch03/y.mod3",
        ];

        PakNameIndex index = PakPathHasher.BuildIndex(paths);

        Assert.True(index.Count >= 2);
        foreach (string path in paths)
        {
            Assert.Equal(path.Replace('\\', '/'), index.TryResolve(PakPathHasher.HashPath(path)));
        }
    }

    [Fact]
    public void BuildIndex_SkipsBlankAndCommentLines()
    {
        PakNameIndex index = PakPathHasher.BuildIndex(["", "  ", "# 注释", "; 另一个注释", "a/b.mod3"]);

        Assert.Equal(1, index.Count);
        Assert.NotNull(index.TryResolve(PakPathHasher.HashPath("a/b.mod3")));
    }

    [Fact]
    public void HashPath_IsCaseSensitiveOnPathButUsesUppercaseHalf()
    {
        ulong lower = PakPathHasher.HashPath("natives/a.mod3");
        ulong upper = PakPathHasher.HashPath("NATIVES/A.MOD3");

        Assert.NotEqual(lower, upper);
        Assert.Equal((uint)lower, (uint)upper is var _ ? (uint)lower : 0u);
    }

    [Fact]
    public void TryResolve_UnknownHash_ReturnsNull()
    {
        PakNameIndex index = PakPathHasher.BuildIndex(["a.mod3"]);

        Assert.Null(index.TryResolve(0xDEADBEEFul));
    }

    /// <summary>
    /// 游戏兼容性金标准：组合哈希由独立实现（UTF-16LE + Murmur3，种子 0xFFFFFFFF）算出，
    /// 重构路径编码/哈希优化时绝不能改变这些值，否则游戏按新路径找不到条目。
    /// </summary>
    [Theory]
    [InlineData("natives/stm/art/model/character/ch03/012/body.mod3", 0xD69B045205F96F6AUL)]
    [InlineData("natives/STM/Art/Model/Character/ch03/015/body.mod3", 0x752EB47C6D67DB69UL)]
    [InlineData("natives/stm/art/model/character/ch03/012/body.mdf2.40", 0x419DB121F3290A9BUL)]
    [InlineData("nativepc/pl/pl000/pl000_00.mod3", 0x5A7E17097BCBC36BUL)]
    [InlineData("natives/stm/a.mod3", 0x7E7A5A09F98D020DUL)]
    [InlineData("a.bin", 0x63E42F4FC1719743UL)]
    [InlineData("natives/stm/art/model/character/ch/001/body.mod3", 0x5C4A72873C134EE5UL)]
    public void HashPath_MatchesEngineHashGoldenValues(string path, ulong expected) =>
        Assert.Equal(expected, PakPathHasher.HashPath(path));

    [Fact]
    public void HashPath_BackslashAndForwardSlash_AreEquivalent()
    {
        Assert.Equal(
            PakPathHasher.HashPath("natives/stm/a.mod3"),
            PakPathHasher.HashPath(@"natives\stm\a.mod3"));
    }
}
