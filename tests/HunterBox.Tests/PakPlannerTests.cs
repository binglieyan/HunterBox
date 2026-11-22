using HunterBox.Domain.Paks;

namespace HunterBox.Tests;

/// <summary>用字典模拟内容指纹，避免测试真的读文件。</summary>
internal sealed class FakeFingerprint : IContentFingerprint
{
    private readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

    public FakeFingerprint With(string fullPath, string content)
    {
        _map[fullPath] = content;
        return this;
    }

    public string GetFingerprint(string fullPath) =>
        _map.TryGetValue(fullPath, out string? value) ? value : fullPath;
}

public class PakPlannerTests
{
    private const string WildsPrefix = TestGames.WildsPatchPrefix;
    private const string RisePrefix = "re_chunk_000.pak.patch_";

    private static PakSource Source(int id, string name, string fileName) =>
        new(id, name, fileName, "/mods/" + id + "/" + fileName);

    [Fact]
    public void Numbered_StartsAfterBaseNumber_WhenGameRootHasNoPatches()
    {
        FakeFingerprint print = new();
        PakPlacementOptions options = new()
        {
            Mode = PakNamingMode.PatchNumbered,
            PatchPrefix = WildsPrefix,
            PatchNumberFloor = 6,
        };

        PakPlan plan = PakPlanner.Plan([Source(1, "A", "a.pak")], options, print);

        Assert.True(plan.CanEnable);
        Assert.Equal(WildsPrefix + "007.pak", Assert.Single(plan.Assignments).FinalName);
    }

    [Fact]
    public void Numbered_ContinuesAfterHighestExistingPatch()
    {
        FakeFingerprint print = new();
        PakPlacementOptions options = new()
        {
            Mode = PakNamingMode.PatchNumbered,
            PatchPrefix = WildsPrefix,
            PatchNumberFloor = 6,
            ExistingGameRootFileNames = [WildsPrefix + "015.pak", "unrelated.pak", WildsPrefix + "003.pak"],
        };

        PakPlan plan = PakPlanner.Plan(
            [Source(1, "A", "a.pak"), Source(2, "B", "b.pak")],
            options,
            print);

        Assert.Equal(
            [WildsPrefix + "016.pak", WildsPrefix + "017.pak"],
            plan.Assignments.Select(a => a.FinalName));
    }

    [Fact]
    public void Numbered_SharesOneNameForIdenticalContent()
    {
        FakeFingerprint print = new FakeFingerprint()
            .With("/mods/1/a.pak", "same")
            .With("/mods/2/b.pak", "same");
        PakPlacementOptions options = new()
        {
            Mode = PakNamingMode.PatchNumbered,
            PatchPrefix = RisePrefix,
            PatchNumberFloor = 1,
        };

        PakPlan plan = PakPlanner.Plan(
            [Source(1, "A", "a.pak"), Source(2, "B", "b.pak")],
            options,
            print);

        Assert.Equal(2, plan.Assignments.Count);
        Assert.Single(plan.Assignments.Select(a => a.FinalName).Distinct(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(RisePrefix + "002.pak", plan.Assignments[0].FinalName);
    }

    [Fact]
    public void KeepOriginal_ReportsConflictForSameNameDifferentContent()
    {
        FakeFingerprint print = new FakeFingerprint()
            .With("/mods/1/cool.pak", "one")
            .With("/mods/2/cool.pak", "two");
        PakPlacementOptions options = new()
        {
            Mode = PakNamingMode.KeepOriginalName,
            PatchPrefix = WildsPrefix,
        };

        PakPlan plan = PakPlanner.Plan(
            [Source(1, "第一个", "cool.pak"), Source(2, "第二个", "cool.pak")],
            options,
            print);

        Assert.False(plan.CanEnable);
        PakConflict conflict = Assert.Single(plan.Conflicts);
        Assert.Equal("cool.pak", conflict.FileName);
        Assert.Equal(2, conflict.Incoming.ModId);
        Assert.Equal(1, conflict.Owner.ModId);
        Assert.Single(plan.Assignments);
    }

    [Fact]
    public void KeepOriginal_AllowsSameNameSameContent()
    {
        FakeFingerprint print = new FakeFingerprint()
            .With("/mods/1/cool.pak", "identical")
            .With("/mods/2/cool.pak", "identical");
        PakPlacementOptions options = new()
        {
            Mode = PakNamingMode.KeepOriginalName,
            PatchPrefix = WildsPrefix,
        };

        PakPlan plan = PakPlanner.Plan(
            [Source(1, "第一个", "cool.pak"), Source(2, "第二个", "cool.pak")],
            options,
            print);

        Assert.True(plan.CanEnable);
        Assert.Empty(plan.Conflicts);
        Assert.All(plan.Assignments, a => Assert.Equal("cool.pak", a.FinalName));
    }

    [Theory]
    [InlineData("re_chunk_000.pak.patch_001.pak", true, 1)]
    [InlineData("re_chunk_000.pak.patch_015.PAK", true, 15)]
    [InlineData("re_chunk_000.pak.patch_15.pak", false, 0)]
    [InlineData("re_chunk_000.pak.patch_1000.pak", true, 1000)]
    [InlineData("re_chunk_000.pak.patch_001.pak.tmp", false, 0)]
    public void TryParsePatchNumber_AcceptsThreeOrFourDigits(string fileName, bool expected, int number)
    {
        bool ok = PakPlanner.TryParsePatchNumber(fileName, RisePrefix, out int parsed);

        Assert.Equal(expected, ok);
        Assert.Equal(number, parsed);
    }
}
