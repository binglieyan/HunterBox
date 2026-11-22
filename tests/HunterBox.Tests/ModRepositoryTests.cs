using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;
using HunterBox.Infrastructure.Storage;

namespace HunterBox.Tests;

public class ModRepositoryTests
{
    private static ModRecord Sample(int id, string name) => new()
    {
        Id = id,
        Index = 0,
        Metadata = new ModMetadata { Name = name, Version = "1.0" },
        Kind = ModKind.LooseFiles,
        RelativeFiles = ["natives/stm/a.tex"],
    };

    [Fact]
    public void Load_ReturnsEmptyWhenIndexMissing()
    {
        using TempDir temp = TempDir.Create();
        ModRepository repo = ModRepository.Load(new BoxPaths(temp.Path));

        Assert.Empty(repo.Mods);
        Assert.Equal(1, repo.NextId());
        Assert.Equal(1, repo.NextIndex());
    }

    [Fact]
    public void SaveAndLoad_RoundTripsRecords()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);

        ModRepository repo = ModRepository.Load(paths);
        repo.Add(Sample(1, "太刀"));
        repo.Add(Sample(2, "大剑"));
        repo.Save();

        ModRepository reloaded = ModRepository.Load(paths);

        Assert.Equal([1, 2], reloaded.Mods.Select(m => m.Id));
        Assert.Equal("太刀", reloaded.Mods[0].Metadata.Name);
        Assert.Equal(["natives/stm/a.tex"], reloaded.Mods[0].RelativeFiles);
        Assert.Equal(3, reloaded.NextId());
    }

    [Fact]
    public void Add_AssignsIncreasingIndex()
    {
        using TempDir temp = TempDir.Create();
        ModRepository repo = ModRepository.Load(new BoxPaths(temp.Path));

        repo.Add(Sample(5, "A"));
        repo.Add(Sample(9, "B"));

        Assert.Equal([1, 2], repo.Mods.Select(m => m.Index));
    }

    [Fact]
    public void Add_RejectsDuplicateId()
    {
        using TempDir temp = TempDir.Create();
        ModRepository repo = ModRepository.Load(new BoxPaths(temp.Path));
        repo.Add(Sample(1, "A"));

        Assert.Throws<InvalidOperationException>(() => repo.Add(Sample(1, "B")));
    }

    [Fact]
    public void NextId_DoesNotReuseDeletedIds()
    {
        using TempDir temp = TempDir.Create();
        ModRepository repo = ModRepository.Load(new BoxPaths(temp.Path));
        repo.Add(Sample(1, "A"));
        repo.Add(Sample(2, "B"));

        Assert.True(repo.Remove(2));

        Assert.Equal(3, repo.NextId());
        Assert.Single(repo.Mods);
    }
}
