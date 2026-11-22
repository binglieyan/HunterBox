using HunterBox.Application.Install;
using HunterBox.Domain.Layout;
using HunterBox.Infrastructure.Archives;
using HunterBox.Infrastructure.Storage;

namespace HunterBox.Tests;

/// <summary>多目录变体包（如 Shop Tweaks）应被识别成多个可选组件。</summary>
public class PackageVariantTests
{
    private static (BoxPaths Paths, ModInstaller Installer, TempDir Temp, string Zip) Setup()
    {
        TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store) = TestBox.OpenRepository(temp);
        ModInstaller installer = new(paths, store, new ArchiveExtractor(), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, "mhws");

        string zip = ZipBuilder.Create(
            temp.Path,
            "variants.zip",
            ("Shop Tweaks/Everything/modinfo.ini", "name=全部商品"),
            ("Shop Tweaks/Everything/natives/stm/shop.tex", "every"),
            ("Shop Tweaks/Consumables Only/modinfo.ini", "name=仅消耗品"),
            ("Shop Tweaks/Consumables Only/natives/stm/shop.tex", "consumables"));

        return (paths, installer, temp, zip);
    }

    [Fact]
    public void Stage_DetectsVariants()
    {
        (_, ModInstaller installer, TempDir temp, string zip) = Setup();
        using (temp)
        {
            using InstallStaging staging = installer.Stage(zip);

            Assert.Equal(2, staging.Variants.Count);
            Assert.Contains(staging.Variants, v => v.Name == "全部商品");
            Assert.Contains(staging.Variants, v => v.Name == "仅消耗品");
        }
    }

    [Fact]
    public void CommitVariant_InstallsOnlyThatVariantAndStripsRoot()
    {
        (BoxPaths paths, ModInstaller installer, TempDir temp, string zip) = Setup();
        using (temp)
        {
            using InstallStaging staging = installer.Stage(zip);
            PackageVariant chosen = staging.Variants.Single(v => v.Name == "仅消耗品");

            InstallResult result = staging.CommitVariant(chosen);

            Assert.Equal(["natives/stm/shop.tex"], result.Record.RelativeFiles);
            string installed = Path.Combine(paths.FilesDirectory(1), "natives", "stm", "shop.tex");
            Assert.True(File.Exists(installed));
            Assert.Equal("consumables", File.ReadAllText(installed));
            Assert.Equal("仅消耗品", result.Record.Metadata.Name);
        }
    }

    [Fact]
    public void CommitVariant_UnknownVariant_Throws()
    {
        (_, ModInstaller installer, TempDir temp, string zip) = Setup();
        using (temp)
        {
            using InstallStaging staging = installer.Stage(zip);
            PackageVariant fake = new("假的", "no-such", new());

            Assert.Throws<InvalidOperationException>(() => staging.CommitVariant(fake));
        }
    }

    [Fact]
    public void SingleFolderWithModInfo_IsNotTreatedAsVariants()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        ModInstaller installer = new(paths, ModRepository.Load(paths), new ArchiveExtractor(), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, "mhws");
        string zip = ZipBuilder.Create(
            temp.Path,
            "single.zip",
            ("CoolMod/modinfo.ini", "name=单个"),
            ("CoolMod/natives/a.tex", "a"));

        using InstallStaging staging = installer.Stage(zip);

        Assert.Empty(staging.Variants);
    }

    [Fact]
    public void VariantsWithoutModInfo_AreDetectedByStructure()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        ModInstaller installer = new(paths, ModRepository.Load(paths), new ArchiveExtractor(), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, "mhws");
        string zip = ZipBuilder.Create(
            temp.Path,
            "structured.zip",
            ("Option A/natives/stm/a.tex", "a"),
            ("Option B/natives/stm/b.tex", "b"));

        using InstallStaging staging = installer.Stage(zip);

        Assert.Equal(2, staging.Variants.Count);
        Assert.Contains(staging.Variants, v => v.Root == "option a");
        Assert.Contains(staging.Variants, v => v.Root == "option b");

        PackageVariant chosen = staging.Variants.Single(v => v.Root == "option b");
        InstallResult result = staging.CommitVariant(chosen);
        Assert.Equal(["natives/stm/b.tex"], result.Record.RelativeFiles);
    }

    [Fact]
    public void KnownContentRoots_AreNotTreatedAsVariants()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        ModInstaller installer = new(paths, ModRepository.Load(paths), new ArchiveExtractor(), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, "mhws");
        string zip = ZipBuilder.Create(
            temp.Path,
            "normal.zip",
            ("natives/stm/a.tex", "a"),
            ("reframework/autorun/x.lua", "print(1)"));

        using InstallStaging staging = installer.Stage(zip);

        Assert.Empty(staging.Variants);
    }
}
