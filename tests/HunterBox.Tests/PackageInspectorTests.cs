using HunterBox.Domain.Mods;

namespace HunterBox.Tests;

public class PackageInspectorTests
{
    [Fact]
    public void LooseFiles_DetectedByNativesFolder()
    {
        PackageInspection result = PackageInspector.Inspect([
            "natives/STM/Art/Model/Character/ch03/x.mod3",
            "modinfo.ini",
        ]);

        Assert.Equal(ModKind.LooseFiles, result.Kind);
        Assert.Null(result.TargetDirectory);
        Assert.True(result.HasModInfo);
    }

    [Fact]
    public void PakFiles_DetectedByRootPak()
    {
        PackageInspection result = PackageInspector.Inspect(["cool.pak", "readme.txt"]);

        Assert.Equal(ModKind.PakFiles, result.Kind);
        Assert.Null(result.TargetDirectory);
    }

    [Fact]
    public void PakInsideSubfolder_IsDetectedAsPak()
    {
        // 「任意层级 .pak 都算 pak MOD」，不再只认根层。
        PackageInspection result = PackageInspector.Inspect(["sub/cool.pak"]);

        Assert.Equal(ModKind.PakFiles, result.Kind);
    }

    [Fact]
    public void Fomod_RecognizesRootLevelConfig()
    {
        PackageInspection result = PackageInspector.Inspect(["ModuleConfig.xml", "natives/a.tex"]);

        Assert.Equal(ModKind.Fomod, result.Kind);
    }

    [Fact]
    public void World_PlDirectory_UsesPackagePathDirectly()
    {
        PackageInspection result = PackageInspector.Inspect(["pl/f_equip.tex", "wp/w_sword.mod3"], "mhw");

        Assert.Equal(ModKind.LooseFiles, result.Kind);
        Assert.Null(result.TargetDirectory);
    }

    [Fact]
    public void World_NativePcDirectory_StaysAtRoot()
    {
        PackageInspection result = PackageInspector.Inspect(["nativePC/pl/f_equip.tex"], "mhw");

        Assert.Equal(ModKind.LooseFiles, result.Kind);
        Assert.Null(result.TargetDirectory);
    }

    [Theory]
    [InlineData("mhr", "weapon/longsword.mod3")]
    [InlineData("mhr", "player/pl100.mod3")]
    [InlineData("mhws", "art/character.mod3")]
    [InlineData("mhws", "gamedesign/quest.bin")]
    public void RiseAndWilds_WeaponArtDirectories_UsePackagePathDirectly(string gameId, string path)
    {
        PackageInspection result = PackageInspector.Inspect([path], gameId);

        Assert.Equal(ModKind.LooseFiles, result.Kind);
        Assert.Null(result.TargetDirectory);
    }

    [Fact]
    public void Rise_AutorunDirectory_UsesPackagePathDirectly()
    {
        PackageInspection result = PackageInspector.Inspect(["autorun/tweak.lua"], "mhr");

        Assert.Equal(ModKind.LooseFiles, result.Kind);
        Assert.Null(result.TargetDirectory);
    }

    [Fact]
    public void DllPackages_AreInsidePackagePath()
    {
        PackageInspection whitelisted = PackageInspector.Inspect(["steam_api64.dll"], "mhws");
        PackageInspection plugin = PackageInspector.Inspect(["firstnatives.dll"], "mhws");

        Assert.Equal(ModKind.RefPlugins, whitelisted.Kind);
        Assert.Null(whitelisted.TargetDirectory);
        Assert.Equal(ModKind.RefPlugins, plugin.Kind);
        Assert.Null(plugin.TargetDirectory);
    }

    [Fact]
    public void RefScripts_UsePackagePathDirectly()
    {
        PackageInspection result = PackageInspector.Inspect(["weapon_tweak.lua"]);

        Assert.Equal(ModKind.RefScripts, result.Kind);
        Assert.Null(result.TargetDirectory);
    }

    [Fact]
    public void RefPlugins_UsePackagePathDirectly()
    {
        PackageInspection result = PackageInspector.Inspect(["firstnatives.dll"]);

        Assert.Equal(ModKind.RefPlugins, result.Kind);
        Assert.Null(result.TargetDirectory);
    }

    [Fact]
    public void FrameworkDll_IsInstalledAsPackagePath()
    {
        PackageInspection result = PackageInspector.Inspect(["dinput8.dll", "reframework/plugins/x.dll"]);

        Assert.Equal(ModKind.LooseFiles, result.Kind);
        Assert.Null(result.TargetDirectory);
    }

    [Fact]
    public void Fomod_RequiresUserSelection()
    {
        PackageInspection result = PackageInspector.Inspect(["fomod/ModuleConfig.xml", "natives/a.tex"]);

        Assert.Equal(ModKind.Fomod, result.Kind);
    }

    [Fact]
    public void SingleReadme_IsLooseFileWithoutPrefix()
    {
        PackageInspection result = PackageInspector.Inspect(["readme.txt"]);

        Assert.Equal(ModKind.Unknown, result.Kind);
        Assert.Null(result.TargetDirectory);
    }

    [Theory]
    [InlineData(@".\natives\a.tex", "natives/a.tex")]
    [InlineData("NATIVES/A.TEX", "natives/a.tex")]
    [InlineData("././reframework/autorun/x.lua", "reframework/autorun/x.lua")]
    public void Normalize_UnifiesSeparatorsAndCase(string input, string expected)
    {
        Assert.Equal(expected, PackageInspector.Normalize(input));
    }

    [Fact]
    public void World_NativePcPluginsDll_StaysInNativePc()
    {
        PackageInspection result = PackageInspector.Inspect(
            ["nativePC/plugins/MHW-DLSS4.dll", "nativePC/plugins/MHW-DLSS4.ini"],
            "mhw");

        Assert.Equal(ModKind.LooseFiles, result.Kind);
        Assert.Null(result.TargetDirectory);
    }

    [Fact]
    public void World_PluginsFolderDll_GetsNativePcPrefix()
    {
        PackageInspection result = PackageInspector.Inspect(["plugins/MHW-DLSS4.dll"], "mhw");

        Assert.Equal(ModKind.LooseFiles, result.Kind);
        Assert.Null(result.TargetDirectory);
    }

    [Fact]
    public void World_BareDll_UsesPackagePathDirectly()
    {
        PackageInspection result = PackageInspector.Inspect(["MHW-DLSS4.dll"], "mhw");

        Assert.Equal(ModKind.RefPlugins, result.Kind);
        Assert.Null(result.TargetDirectory);
    }

    [Fact]
    public void World_BareLua_UsesPackagePathDirectly()
    {
        PackageInspection result = PackageInspector.Inspect(["script.lua"], "mhw");

        Assert.Equal(ModKind.RefScripts, result.Kind);
        Assert.Null(result.TargetDirectory);
    }
}
