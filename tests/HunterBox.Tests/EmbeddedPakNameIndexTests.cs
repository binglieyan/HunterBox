using HunterBox.Domain.Paks;
using HunterBox.Infrastructure.Paks;

namespace HunterBox.Tests;

public class EmbeddedPakNameIndexTests
{
    [Fact]
    public void LoadWilds_ReturnsLargeIndex()
    {
        PakNameIndex index = EmbeddedPakNameIndex.LoadWilds();

        Assert.True(index.Count > 40_000, $"内置清单条目过少：{index.Count}");
    }

    [Fact]
    public void LoadWilds_ResolvesKnownModelPath()
    {
        const string path = "natives/STM/Art/Model/Character/ch00/000/0000/ch00_000_0000.mdf2.45";
        PakNameIndex index = EmbeddedPakNameIndex.LoadWilds();

        Assert.Equal(path, index.TryResolve(PakPathHasher.HashPath(path)));
    }
}
