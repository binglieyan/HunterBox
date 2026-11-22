using HunterBox.Application.Install;
using HunterBox.Domain.Layout;
using HunterBox.Infrastructure.Archives;
using HunterBox.Infrastructure.Storage;

namespace HunterBox.Tests;

/// <summary>FOMOD 包按选项安装（对应安装对话框的 分组面板）。</summary>
public class FomodInstallTests
{
    private const string Config = """
        <config>
          <moduleName>组件包</moduleName>
          <installSteps>
            <installStep name="主体">
              <optionalFileGroups>
                <group name="体型" type="SelectExactlyOne">
                  <plugins>
                    <plugin name="普通"><files><file source="data/normal.mod3" destination="natives/normal.mod3" /></files></plugin>
                    <plugin name="大型"><files><file source="data/large.mod3" destination="natives/large.mod3" /></files></plugin>
                  </plugins>
                </group>
              </optionalFileGroups>
            </installStep>
          </installSteps>
        </config>
        """;

    private static (BoxPaths Paths, ModInstaller Installer, TempDir Temp, string Zip) Setup()
    {
        TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store) = TestBox.OpenRepository(temp);
        ModInstaller installer = new(paths, store, new ArchiveExtractor(), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, "mhws");
        string zip = ZipBuilder.Create(
            temp.Path,
            "fomod.zip",
            ("fomod/ModuleConfig.xml", Config),
            ("data/normal.mod3", "normal"),
            ("data/large.mod3", "large"));
        return (paths, installer, temp, zip);
    }

    [Fact]
    public void Stage_ExposesFomodConfig()
    {
        (_, ModInstaller installer, TempDir temp, string zip) = Setup();
        using (temp)
        {
            using InstallStaging staging = installer.Stage(zip);
            Assert.NotNull(staging.Fomod);
            Assert.Single(staging.Fomod!.Groups);
            Assert.Equal(2, staging.Fomod.Groups[0].Plugins.Count);
        }
    }

    [Fact]
    public void CommitFomod_InstallsOnlySelectedPluginFiles()
    {
        (BoxPaths paths, ModInstaller installer, TempDir temp, string zip) = Setup();
        using (temp)
        {
            using InstallStaging staging = installer.Stage(zip);
            InstallResult result = FomodSelectionTestTools.CommitByName(staging, ["大型"]);

            Assert.Equal(["natives/large.mod3"], result.Record.RelativeFiles);
            Assert.True(File.Exists(Path.Combine(paths.FilesDirectory(1), "natives", "large.mod3")));
            Assert.False(File.Exists(Path.Combine(paths.FilesDirectory(1), "natives", "normal.mod3")));
        }
    }

    [Fact]
    public void CommitFomod_NoSelection_Throws()
    {
        // SelectAny 组没有默认选中项，空选择经由公开 SetSelected 构造，提交时必须失败。
        const string config = """
            <config>
              <moduleName>可选项包</moduleName>
              <installSteps>
                <installStep name="主体">
                  <optionalFileGroups>
                    <group name="体型" type="SelectAny">
                      <plugins>
                        <plugin name="普通"><files><file source="data/normal.mod3" destination="natives/normal.mod3" /></files></plugin>
                      </plugins>
                    </group>
                  </optionalFileGroups>
                </installStep>
              </installSteps>
            </config>
            """;
        (_, ModInstaller installer, TempDir temp, _) = Setup();
        using (temp)
        {
            string zip = ZipBuilder.Create(
                temp.Path,
                "fomod-any.zip",
                ("fomod/ModuleConfig.xml", config),
                ("data/normal.mod3", "normal"));
            using InstallStaging staging = installer.Stage(zip);
            Assert.Throws<InvalidOperationException>(() => FomodSelectionTestTools.CommitByName(staging, []));
        }
    }
}
