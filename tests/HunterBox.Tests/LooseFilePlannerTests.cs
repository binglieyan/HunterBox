using HunterBox.Domain.Mods;

namespace HunterBox.Tests;

public class LooseFilePlannerTests
{
    private const string File = "natives/stm/art/model/character/ch03/x.mod3";

    private static MountContext Context(
        bool exists,
        int? owner = null,
        bool hasBackup = false,
        bool claimedByOther = false) => new()
        {
            ExistsInGame = _ => exists,
            OwnerOf = _ => owner,
            HasBackup = _ => hasBackup,
            ClaimedByOtherEnabledMod = _ => claimedByOther,
        };

    [Fact]
    public void PlanEnable_CopiesNewFile()
    {
        IReadOnlyList<FileOperation> ops = LooseFilePlanner.PlanEnable([File], MountContext.Empty);

        FileOperation op = Assert.Single(ops);
        Assert.Equal(FileOperationKind.Copy, op.Kind);
        Assert.Equal(File, op.RelativePath);
    }

    [Fact]
    public void PlanEnable_BacksUpUnmanagedGameFile()
    {
        IReadOnlyList<FileOperation> ops = LooseFilePlanner.PlanEnable([File], Context(exists: true));

        Assert.Equal(FileOperationKind.BackupThenCopy, Assert.Single(ops).Kind);
    }

    [Fact]
    public void PlanEnable_OverwritesFileOwnedByBox()
    {
        IReadOnlyList<FileOperation> ops = LooseFilePlanner.PlanEnable([File], Context(exists: true, owner: 5));

        Assert.Equal(FileOperationKind.Copy, Assert.Single(ops).Kind);
    }

    [Fact]
    public void PlanDisable_DeletesWhenNoBackupAndNobodyElseNeedsIt()
    {
        IReadOnlyList<FileOperation> ops = LooseFilePlanner.PlanDisable([File], Context(exists: true));

        Assert.Equal(FileOperationKind.Delete, Assert.Single(ops).Kind);
    }

    [Fact]
    public void PlanDisable_RestoresBackupWhenPresent()
    {
        IReadOnlyList<FileOperation> ops = LooseFilePlanner.PlanDisable([File], Context(exists: true, hasBackup: true));

        Assert.Equal(FileOperationKind.RestoreBackup, Assert.Single(ops).Kind);
    }

    [Fact]
    public void PlanDisable_KeepsFileNeededByAnotherEnabledMod()
    {
        IReadOnlyList<FileOperation> ops = LooseFilePlanner.PlanDisable(
            [File],
            Context(exists: true, claimedByOther: true));

        Assert.Empty(ops);
    }

    [Fact]
    public void PlanDisable_IgnoresMissingFile()
    {
        Assert.Empty(LooseFilePlanner.PlanDisable([File], Context(exists: false)));
    }

    [Fact]
    public void PlanDisable_RestoresBackupEvenWhenTargetMissing()
    {
        // 目标被外部删除但备份仍在：必须还原备份，否则原文件永久留在备份目录（C-05）。
        IReadOnlyList<FileOperation> ops = LooseFilePlanner.PlanDisable(
            [File],
            Context(exists: false, hasBackup: true));

        Assert.Equal(FileOperationKind.RestoreBackup, Assert.Single(ops).Kind);
    }

    [Fact]
    public void BothPlans_NormalizeAndDeduplicateInput()
    {
        string[] messy = [@"NATIVES\STM\x.mod3", "./natives/stm/x.mod3"];

        IReadOnlyList<FileOperation> ops = LooseFilePlanner.PlanEnable(messy, MountContext.Empty);

        Assert.Equal(FileOperationKind.Copy, Assert.Single(ops).Kind);
        Assert.Equal("natives/stm/x.mod3", ops[0].RelativePath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../evil.dll")]
    [InlineData("natives/../../evil.dll")]
    [InlineData("/absolute/x.mod3")]
    [InlineData(@"C:\absolute\x.mod3")]
    public void BothPlans_EmptyOrUnsafePath_ThrowsArgumentException(string path)
    {
        Assert.Throws<ArgumentException>(() => LooseFilePlanner.PlanEnable([path], MountContext.Empty));
        Assert.Throws<ArgumentException>(() => LooseFilePlanner.PlanDisable([path], Context(exists: true)));
    }

    [Fact]
    public void BothPlans_NullPathElement_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => LooseFilePlanner.PlanEnable([null!], MountContext.Empty));
        Assert.Throws<ArgumentException>(() => LooseFilePlanner.PlanDisable([null!], Context(exists: true)));
    }
}
