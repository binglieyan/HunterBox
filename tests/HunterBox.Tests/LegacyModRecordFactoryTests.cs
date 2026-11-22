using HunterBox.Application.Migration;
using HunterBox.Domain.Mods;

namespace HunterBox.Tests;

/// <summary>A-03：旧数据导入的记录构建规则可在 Application 层测试。</summary>
public class LegacyModRecordFactoryTests
{
    private static readonly DateTimeOffset FixedTime = new(2026, 11, 18, 3, 0, 0, TimeSpan.Zero);

    private static readonly PackageInspection Inspection = new(ModKind.LooseFiles, null, false, []);

    [Fact]
    public void Create_UsesNameThenModuleNameThenFallback()
    {
        ModRecord named = LegacyModRecordFactory.Create(
            7,
            TestGames.Wilds.Id,
            Descriptor(name: "真名", moduleName: "模块名"),
            Inspection,
            ["natives/a.mod3"],
            FixedTime);
        ModRecord moduleNamed = LegacyModRecordFactory.Create(
            8,
            TestGames.Wilds.Id,
            Descriptor(name: null, moduleName: "模块名"),
            Inspection,
            ["natives/a.mod3"],
            FixedTime);
        ModRecord fallback = LegacyModRecordFactory.Create(
            9,
            TestGames.Wilds.Id,
            Descriptor(name: null, moduleName: null),
            Inspection,
            ["natives/a.mod3"],
            FixedTime);

        Assert.Equal("真名", named.Metadata.Name);
        Assert.Equal("模块名", moduleNamed.Metadata.Name);
        Assert.Equal("MOD 9", fallback.Metadata.Name);
    }

    [Fact]
    public void Create_AlwaysImportsAsDisabled()
    {
        ModRecord record = LegacyModRecordFactory.Create(
            1,
            TestGames.Wilds.Id,
            Descriptor(name: "x", moduleName: null),
            Inspection,
            ["natives/a.mod3"],
            FixedTime);

        Assert.False(record.Enabled);
        Assert.Equal(FixedTime, record.InstalledAt);
        Assert.Equal(TestGames.Wilds.Id, record.GameId);
    }

    [Fact]
    public void Create_NormalizesFilesAndMapsZeroNexusIdToNull()
    {
        ModRecord record = LegacyModRecordFactory.Create(
            2,
            TestGames.Wilds.Id,
            Descriptor(name: "x", moduleName: null, nexusId: 0),
            Inspection,
            [@"natives\stm\a.mod3", "natives/stm/b.mod3"],
            FixedTime);

        Assert.Equal(["natives/stm/a.mod3", "natives/stm/b.mod3"], record.RelativeFiles);
        Assert.Null(record.Metadata.NexusId);
    }

    [Fact]
    public void Create_KeepsInspectionTargetDirectoryAndKind()
    {
        PackageInspection inspection = new(ModKind.PakFiles, @"natives\stm", true, ["提示"]);
        ModRecord record = LegacyModRecordFactory.Create(
            3,
            TestGames.World.Id,
            Descriptor(name: "x", moduleName: null),
            inspection,
            ["a.pak"],
            FixedTime);

        Assert.Equal(ModKind.PakFiles, record.Kind);
        Assert.Equal(@"natives\stm", record.TargetDirectory);
    }

    private static LegacyModDescriptor Descriptor(
        string? name,
        string? moduleName,
        int nexusId = 0) =>
        new(name, moduleName, "模型", "1.0", "作者", nexusId, "https://example.com", "备注");
}
