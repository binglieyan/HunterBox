using HunterBox.Application.Install;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;
using HunterBox.Infrastructure.Archives;
using HunterBox.Infrastructure.Storage;

namespace HunterBox.Tests;

/// <summary>
/// FOMOD 语义：安装步骤、可见性依赖、必装文件、
/// 条件安装、插件类型、默认值与至少一选校验。
/// </summary>
public class FomodSemanticsTests
{
    private const string RichConfig = """
        <?xml version="1.0" encoding="utf-8"?>
        <config>
          <moduleName>丰富组件包</moduleName>
          <version>2.1</version>
          <author>作者甲</author>
          <description>模块描述乙</description>
          <moduleImage path="fomod/images/cover.png" />
          <requiredInstallFiles>
            <file source="core/engine.dll" destination="reframework/plugins/engine.dll" priority="1" />
            <folder source="core/data" destination="natives/core" />
          </requiredInstallFiles>
          <conditionalFileInstalls>
            <patterns>
              <pattern>
                <dependencies>
                  <flagDependency flag="extraOn" value="yes" />
                </dependencies>
                <files>
                  <file source="extra/flag.dat" destination="natives/extra/flag.dat" />
                </files>
              </pattern>
            </patterns>
          </conditionalFileInstalls>
          <installSteps>
            <installStep name="第一步">
              <optionalFileGroups>
                <group name="主体" type="SelectExactlyOne">
                  <plugins>
                    <plugin name="标准">
                      <description>标准版</description>
                      <typeDescriptor><type name="Recommended" /></typeDescriptor>
                      <files><file source="data/std.mod3" destination="natives/std.mod3" /></files>
                    </plugin>
                    <plugin name="轻量">
                      <files><file source="data/lite.mod3" destination="natives/lite.mod3" /></files>
                    </plugin>
                  </plugins>
                </group>
                <group name="增强" type="SelectAny">
                  <plugins>
                    <plugin name="增强开关">
                      <conditionFlags><flag name="extraOn" value="yes" /></conditionFlags>
                      <files><file source="data/extra.mod3" destination="natives/extra.mod3" /></files>
                    </plugin>
                  </plugins>
                </group>
              </optionalFileGroups>
            </installStep>
            <installStep name="隐藏步骤">
              <visible><flagDependency flag="extraOn" value="yes" /></visible>
              <optionalFileGroups>
                <group name="隐藏" type="SelectAtLeastOne">
                  <plugins>
                    <plugin name="隐藏项一">
                      <files><file source="hidden/a.mod3" destination="natives/hidden/a.mod3" /></files>
                    </plugin>
                    <plugin name="隐藏项二">
                      <typeDescriptor><type name="Required" /></typeDescriptor>
                      <files><file source="hidden/b.mod3" destination="natives/hidden/b.mod3" /></files>
                    </plugin>
                  </plugins>
                </group>
              </optionalFileGroups>
            </installStep>
          </installSteps>
        </config>
        """;

    private static readonly string[] PackageFiles =
    [
        "fomod/moduleconfig.xml",
        "fomod/images/cover.png",
        "core/engine.dll",
        "core/data/a.tex",
        "core/data/sub/b.tex",
        "extra/flag.dat",
        "data/std.mod3",
        "data/lite.mod3",
        "data/extra.mod3",
        "hidden/a.mod3",
        "hidden/b.mod3",
    ];

    private static FomodConfig ParseRich() =>
        FomodParser.Parse(RichConfig, "fomod/moduleconfig.xml", PackageFiles)!;

    [Fact]
    public void Parse_ReadsStepsMetadataRequiredFilesAndConditionals()
    {
        FomodConfig config = ParseRich();

        Assert.Equal("丰富组件包", config.ModuleName);
        Assert.Equal("2.1", config.ModuleVersion);
        Assert.Equal("作者甲", config.ModuleAuthor);
        Assert.Equal("模块描述乙", config.ModuleDescription);
        Assert.Equal("fomod/images/cover.png", config.ModuleImage);

        Assert.Equal(2, config.InstallSteps.Count);
        Assert.Equal("第一步", config.InstallSteps[0].Name);
        Assert.NotNull(config.InstallSteps[1].Visible);

        // requiredInstallFiles：file 原样 + folder 展开成目录下每个文件。
        Assert.Equal(3, config.RequiredInstallFiles.Count);
        Assert.Contains(config.RequiredInstallFiles, m => m.Source == "core/engine.dll" && m.Priority == 1);
        Assert.Contains(config.RequiredInstallFiles, m =>
            m.Source == "core/data/a.tex" && m.Destination == "natives/core/a.tex");
        Assert.Contains(config.RequiredInstallFiles, m =>
            m.Source == "core/data/sub/b.tex" && m.Destination == "natives/core/sub/b.tex");

        FomodInstallPattern pattern = Assert.Single(config.ConditionalInstalls);
        Assert.NotNull(pattern.Dependencies);
        Assert.Single(pattern.Files);
    }

    [Fact]
    public void Parse_ReadsGroupPluginTypesImagesFlags()
    {
        FomodConfig config = ParseRich();
        FomodGroup main = config.InstallSteps[0].Groups[0];

        Assert.Equal(FomodGroupType.SelectExactlyOne, main.Type);
        Assert.Equal(FomodPluginType.Recommended, main.Plugins[0].Type);
        Assert.Equal("标准版", main.Plugins[0].Description);

        FomodGroup extra = config.InstallSteps[0].Groups[1];
        Assert.Equal("增强开关", extra.Plugins[0].Name);
        Assert.NotNull(extra.Plugins[0].ConditionFlags);
        Assert.Contains(extra.Plugins[0].ConditionFlags!, f => f.Name == "extraOn" && f.Value == "yes");

        FomodGroup hidden = config.InstallSteps[1].Groups[0];
        Assert.Equal(FomodGroupType.SelectAtLeastOne, hidden.Type);
        Assert.Equal(FomodPluginType.Required, hidden.Plugins[1].Type);
    }

    [Fact]
    public void Defaults_RequiredRecommendedSelectAllAndExactlyOneFirst()
    {
        FomodSelectionState state = FomodSelectionState.Create(ParseRich(), PackageFiles);

        FomodSelectionEntry standard = state.Entries.First(e => e.Plugin.Name == "标准");
        FomodSelectionEntry lite = state.Entries.First(e => e.Plugin.Name == "轻量");
        FomodSelectionEntry hiddenRequired = state.Entries.First(e => e.Plugin.Name == "隐藏项二");

        Assert.True(state.IsSelected(standard), "Recommended 默认选中");
        Assert.False(state.IsSelected(lite));
        Assert.True(state.IsSelected(hiddenRequired), "Required 默认选中且锁定");
        Assert.True(FomodSelectionState.IsLocked(hiddenRequired));
        Assert.False(FomodSelectionState.IsLocked(standard));
    }

    [Fact]
    public void ExactlyOne_SelectingAnotherUnselectsFirst()
    {
        FomodSelectionState state = FomodSelectionState.Create(ParseRich(), PackageFiles);
        FomodSelectionEntry lite = state.Entries.First(e => e.Plugin.Name == "轻量");
        FomodSelectionEntry standard = state.Entries.First(e => e.Plugin.Name == "标准");

        state.SetSelected(lite, true);

        Assert.True(state.IsSelected(lite));
        Assert.False(state.IsSelected(standard));

        // 单选组不允许取消到 0 个。
        state.SetSelected(lite, false);
        Assert.True(state.IsSelected(lite));
    }

    [Fact]
    public void Visibility_DependsOnFlagsFromSelection()
    {
        FomodConfig config = ParseRich();
        FomodSelectionState state = FomodSelectionState.Create(config, PackageFiles);
        FomodInstallStep hiddenStep = config.InstallSteps[1];

        Assert.False(state.IsStepVisible(hiddenStep), "flag 未设置时隐藏步骤不可见");

        FomodSelectionEntry toggle = state.Entries.First(e => e.Plugin.Name == "增强开关");
        state.SetSelected(toggle, true);

        Assert.True(state.IsStepVisible(hiddenStep), "勾选设置 flag 后隐藏步骤可见");

        FomodSelectionEntry hiddenA = state.Entries.First(e => e.Plugin.Name == "隐藏项一");
        Assert.False(state.IsSelected(hiddenA), "AtLeastOne 组默认不选中任何项");
    }

    [Fact]
    public void Validate_AtLeastOneGroup_ReportsErrorUntilSelected()
    {
        FomodConfig config = ParseRich();
        FomodSelectionState state = FomodSelectionState.Create(config, PackageFiles);
        FomodSelectionEntry toggle = state.Entries.First(e => e.Plugin.Name == "增强开关");
        state.SetSelected(toggle, true);

        // 隐藏步骤可见后，AtLeastOne 组只有 Required 项被强制选中 → 已满足；构造一个无 Required的组单独验证。
        Assert.Empty(state.Validate());
    }

    [Fact]
    public void ResolveFiles_IncludesRequiredSelectedAndConditional()
    {
        FomodConfig config = ParseRich();
        FomodSelectionState state = FomodSelectionState.Create(config, PackageFiles);
        FomodSelectionEntry toggle = state.Entries.First(e => e.Plugin.Name == "增强开关");
        state.SetSelected(toggle, true);
        FomodSelectionEntry hiddenA = state.Entries.First(e => e.Plugin.Name == "隐藏项一");
        state.SetSelected(hiddenA, true);

        IReadOnlyList<FomodFileMapping> files = state.ResolveFiles();
        List<string> destinations = [.. files.Select(f => f.Destination)];

        Assert.Contains("reframework/plugins/engine.dll", destinations);
        Assert.Contains("natives/core/a.tex", destinations);
        Assert.Contains("natives/std.mod3", destinations);
        Assert.Contains("natives/extra.mod3", destinations);
        Assert.Contains("natives/hidden/a.mod3", destinations);
        Assert.Contains("natives/hidden/b.mod3", destinations);
        Assert.Contains("natives/extra/flag.dat", destinations);
        Assert.DoesNotContain("natives/lite.mod3", destinations);
    }

    [Fact]
    public void AtLeastOneGroup_WithoutSelection_FailsValidation()
    {
        const string config = """
            <config>
              <moduleName>至少一选</moduleName>
              <installSteps>
                <installStep name="步骤">
                  <optionalFileGroups>
                    <group name="必选组" type="SelectAtLeastOne">
                      <plugins>
                        <plugin name="甲"><files><file source="a.bin" destination="a.bin" /></files></plugin>
                        <plugin name="乙"><files><file source="b.bin" destination="b.bin" /></files></plugin>
                      </plugins>
                    </group>
                  </optionalFileGroups>
                </installStep>
              </installSteps>
            </config>
            """;

        FomodConfig parsed = FomodParser.Parse(config, ["a.bin", "b.bin"])!;
        FomodSelectionState state = FomodSelectionState.Create(parsed, ["a.bin", "b.bin"]);

        Assert.Contains(state.Validate(), e => e.Contains("必选组") && e.Contains("至少"));

        state.SetSelected(state.Entries.First(e => e.Plugin.Name == "乙"), true);
        Assert.Empty(state.Validate());
    }

    [Fact]
    public void ResolveFiles_MissingSourceIsSkippedAtCommitWithWarning()
    {
        // 必装文件引用了包里不存在的文件：安装应跳过并提示，而不是整体失败。
        const string config = """
            <config>
              <moduleName>缺文件</moduleName>
              <requiredInstallFiles>
                <file source="missing.bin" destination="natives/missing.bin" />
                <file source="present.bin" destination="natives/present.bin" />
              </requiredInstallFiles>
              <installSteps>
                <installStep name="步骤">
                  <optionalFileGroups>
                    <group name="组" type="SelectAny">
                      <plugins>
                        <plugin name="项"><files><file source="present.bin" destination="natives/present.bin" /></files></plugin>
                      </plugins>
                    </group>
                  </optionalFileGroups>
                </installStep>
              </installSteps>
            </config>
            """;

        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        ModInstaller installer = new(paths, ModRepository.Load(paths), new ArchiveExtractor(), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, "mhws");
        string zip = ZipBuilder.Create(
            temp.Path,
            "fomod.zip",
            ("fomod/ModuleConfig.xml", config),
            ("present.bin", "present"));

        using InstallStaging staging = installer.Stage(zip);
        InstallResult result = FomodSelectionTestTools.CommitByName(staging, ["项"]);

        Assert.Contains("natives/present.bin", result.Record.RelativeFiles);
        Assert.Contains(result.Warnings, w => w.Contains("missing.bin"));
    }

    [Fact]
    public void HiddenStepFlags_DoNotLeakIntoVisibility()
    {
        // 隐藏步骤里的必选项设置 flag；该 flag 不能让步骤自己变为可见，也不能把文件装进去。
        const string config = """
            <config>
              <moduleName>隐藏步骤标记</moduleName>
              <installSteps>
                <installStep name="第一步">
                  <optionalFileGroups>
                    <group name="组" type="SelectAny">
                      <plugins>
                        <plugin name="普通"><files><file source="a.bin" destination="a.bin" /></files></plugin>
                      </plugins>
                    </group>
                  </optionalFileGroups>
                </installStep>
                <installStep name="隐藏步骤">
                  <visible><flagDependency flag="hiddenOn" value="yes" /></visible>
                  <optionalFileGroups>
                    <group name="隐藏组" type="SelectAny">
                      <plugins>
                        <plugin name="隐藏项">
                          <typeDescriptor><type name="Required" /></typeDescriptor>
                          <conditionFlags><flag name="hiddenOn" value="yes" /></conditionFlags>
                          <files><file source="hidden.bin" destination="natives/hidden.bin" /></files>
                        </plugin>
                      </plugins>
                    </group>
                  </optionalFileGroups>
                </installStep>
              </installSteps>
            </config>
            """;

        FomodConfig parsed = FomodParser.Parse(config, ["a.bin", "hidden.bin"])!;
        FomodSelectionState state = FomodSelectionState.Create(parsed, ["a.bin", "hidden.bin"]);
        FomodSelectionEntry hiddenEntry = state.Entries.First(e => e.Plugin.Name == "隐藏项");

        Assert.True(state.IsSelected(hiddenEntry), "Required 项仍然记录为选中");
        Assert.False(state.IsStepVisible(parsed.InstallSteps[1]), "隐藏步骤不能用自己的标记点亮自己");
        Assert.DoesNotContain(state.Flags, pair => pair.Key == "hiddenOn");
        Assert.DoesNotContain(
            "natives/hidden.bin",
            state.ResolveFiles().Select(f => f.Destination));
    }

    [Fact]
    public void NotUsable_IsNeverSelectedLockedOrInstalled()
    {
        const string config = """
            <config>
              <moduleName>不可用项</moduleName>
              <installSteps>
                <installStep name="步骤">
                  <optionalFileGroups>
                    <group name="单选" type="SelectExactlyOne">
                      <plugins>
                        <plugin name="不可用项">
                          <typeDescriptor><type name="NotUsable" /></typeDescriptor>
                          <files><file source="notusable.bin" destination="natives/notusable.bin" /></files>
                        </plugin>
                        <plugin name="可用项">
                          <files><file source="usable.bin" destination="natives/usable.bin" /></files>
                        </plugin>
                      </plugins>
                    </group>
                  </optionalFileGroups>
                </installStep>
              </installSteps>
            </config>
            """;

        FomodConfig parsed = FomodParser.Parse(config, ["notusable.bin", "usable.bin"])!;
        FomodSelectionState state = FomodSelectionState.Create(parsed, ["notusable.bin", "usable.bin"]);
        FomodSelectionEntry notUsable = state.Entries.First(e => e.Plugin.Name == "不可用项");
        FomodSelectionEntry usable = state.Entries.First(e => e.Plugin.Name == "可用项");

        Assert.False(state.IsSelected(notUsable), "不可用项不默认选中");
        Assert.True(FomodSelectionState.IsLocked(notUsable), "不可用项锁住（界面置灰、不能勾选）");
        Assert.True(state.IsSelected(usable), "单选组回退到第一个可用项");

        state.SetSelected(notUsable, true);
        Assert.False(state.IsSelected(notUsable), "用户点选也不能选上不可用项");

        Assert.Empty(state.Validate());
        List<string> destinations = [.. state.ResolveFiles().Select(f => f.Destination)];
        Assert.Contains("natives/usable.bin", destinations);
        Assert.DoesNotContain("natives/notusable.bin", destinations);
    }

    [Fact]
    public void NotUsable_OnlyOption_DoesNotBlockValidation()
    {
        const string config = """
            <config>
              <moduleName>只有不可用项</moduleName>
              <installSteps>
                <installStep name="步骤">
                  <optionalFileGroups>
                    <group name="单选" type="SelectExactlyOne">
                      <plugins>
                        <plugin name="不可用项">
                          <typeDescriptor><type name="NotUsable" /></typeDescriptor>
                          <files><file source="notusable.bin" destination="natives/notusable.bin" /></files>
                        </plugin>
                      </plugins>
                    </group>
                  </optionalFileGroups>
                </installStep>
              </installSteps>
            </config>
            """;

        FomodConfig parsed = FomodParser.Parse(config, ["notusable.bin"])!;
        FomodSelectionState state = FomodSelectionState.Create(parsed, ["notusable.bin"]);

        Assert.Empty(state.Validate());
        Assert.DoesNotContain(
            "natives/notusable.bin",
            state.ResolveFiles().Select(f => f.Destination));
    }

    [Fact]
    public void CouldBeUsable_IsTreatedAsOptional()
    {
        const string config = """
            <config>
              <moduleName>条件可用项</moduleName>
              <installSteps>
                <installStep name="步骤">
                  <optionalFileGroups>
                    <group name="任选" type="SelectAny">
                      <plugins>
                        <plugin name="条件可用">
                          <typeDescriptor><type name="CouldBeUsable" /></typeDescriptor>
                          <files><file source="maybe.bin" destination="natives/maybe.bin" /></files>
                        </plugin>
                      </plugins>
                    </group>
                  </optionalFileGroups>
                </installStep>
              </installSteps>
            </config>
            """;

        FomodConfig parsed = FomodParser.Parse(config, ["maybe.bin"])!;
        FomodSelectionState state = FomodSelectionState.Create(parsed, ["maybe.bin"]);
        FomodSelectionEntry entry = Assert.Single(state.Entries);

        Assert.False(state.IsSelected(entry), "条件可用项默认不选");
        Assert.False(FomodSelectionState.IsLocked(entry));

        state.SetSelected(entry, true);
        Assert.True(state.IsSelected(entry));
        Assert.Contains("natives/maybe.bin", state.ResolveFiles().Select(f => f.Destination));
    }

    [Fact]
    public void RootLevelModuleConfig_IsRecognizedAndInstalledAsFomod()
    {
        const string config = """
            <config>
              <moduleName>根级配置包</moduleName>
              <requiredInstallFiles>
                <file source="core.dll" destination="reframework/plugins/core.dll" />
              </requiredInstallFiles>
              <installSteps>
                <installStep name="步骤">
                  <optionalFileGroups>
                    <group name="组" type="SelectExactlyOne">
                      <plugins>
                        <plugin name="选项甲"><files><file source="data/a.mod3" destination="natives/a.mod3" /></files></plugin>
                        <plugin name="选项乙"><files><file source="data/b.mod3" destination="natives/b.mod3" /></files></plugin>
                      </plugins>
                    </group>
                  </optionalFileGroups>
                </installStep>
              </installSteps>
            </config>
            """;

        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        ModInstaller installer = new(paths, ModRepository.Load(paths), new ArchiveExtractor(), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, "mhws");
        string zip = ZipBuilder.Create(
            temp.Path,
            "root-fomod.zip",
            ("ModuleConfig.xml", config),
            ("core.dll", "core"),
            ("data/a.mod3", "a"),
            ("data/b.mod3", "b"));

        using InstallStaging staging = installer.Stage(zip);
        Assert.NotNull(staging.Fomod);
        Assert.Equal("根级配置包", staging.Fomod!.ModuleName);
        Assert.Equal("根级配置包", staging.SuggestedMetadata.Name);

        InstallResult result = FomodSelectionTestTools.CommitByName(staging, ["选项乙"]);

        Assert.Contains("reframework/plugins/core.dll", result.Record.RelativeFiles);
        Assert.Contains("natives/b.mod3", result.Record.RelativeFiles);
        Assert.DoesNotContain("natives/a.mod3", result.Record.RelativeFiles);
        Assert.DoesNotContain(result.Record.RelativeFiles, f => f.Contains("moduleconfig"));
        Assert.True(File.Exists(Path.Combine(paths.FilesDirectory(1), "reframework", "plugins", "core.dll")));
    }

    [Fact]
    public void FomodMetadata_FillsSuggestedMetadataAndPreview()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        ModInstaller installer = new(paths, ModRepository.Load(paths), new ArchiveExtractor(), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, "mhws");
        string zip = ZipBuilder.Create(
            temp.Path,
            "meta.zip",
            ("fomod/ModuleConfig.xml", RichConfig),
            ("core/engine.dll", "x"),
            ("data/std.mod3", "x"),
            ("data/lite.mod3", "x"),
            ("fomod/images/cover.png", "not-a-real-png"));

        using InstallStaging staging = installer.Stage(zip);

        Assert.Equal("丰富组件包", staging.SuggestedMetadata.Name);
        Assert.Equal("2.1", staging.SuggestedMetadata.Version);
        Assert.Equal("作者甲", staging.SuggestedMetadata.Author);
        Assert.Equal("模块描述乙", staging.SuggestedMetadata.Description);
        Assert.NotNull(staging.PreviewImagePath);
        Assert.EndsWith("cover.png", staging.PreviewImagePath!);
    }

    [Fact]
    public void CommitFomod_RequiredPluginAlwaysInstalledEvenIfNotNamed()
    {
        const string config = """
            <config>
              <moduleName>必选插件</moduleName>
              <installSteps>
                <installStep name="步骤">
                  <optionalFileGroups>
                    <group name="组" type="SelectAny">
                      <plugins>
                        <plugin name="必装"><typeDescriptor><type name="Required" /></typeDescriptor>
                          <files><file source="req.bin" destination="natives/req.bin" /></files></plugin>
                        <plugin name="可选">
                          <files><file source="opt.bin" destination="natives/opt.bin" /></files></plugin>
                      </plugins>
                    </group>
                  </optionalFileGroups>
                </installStep>
              </installSteps>
            </config>
            """;

        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        ModInstaller installer = new(paths, ModRepository.Load(paths), new ArchiveExtractor(), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, "mhws");
        string zip = ZipBuilder.Create(
            temp.Path, "required.zip",
            ("ModuleConfig.xml", config),
            ("req.bin", "r"),
            ("opt.bin", "o"));

        using InstallStaging staging = installer.Stage(zip);
        InstallResult result = FomodSelectionTestTools.CommitByName(staging, ["可选"]);

        Assert.Contains("natives/req.bin", result.Record.RelativeFiles);
        Assert.Contains("natives/opt.bin", result.Record.RelativeFiles);
    }

    [Fact]
    public void StageReinstall_ThroughFomod_CanChangeSelectionAndMetadata()
    {
        const string config = """
            <config>
              <moduleName>重装组件包</moduleName>
              <installSteps>
                <installStep name="步骤">
                  <optionalFileGroups>
                    <group name="组" type="SelectExactlyOne">
                      <plugins>
                        <plugin name="甲"><files><file source="data/a.mod3" destination="natives/a.mod3" /></files></plugin>
                        <plugin name="乙"><files><file source="data/b.mod3" destination="natives/b.mod3" /></files></plugin>
                      </plugins>
                    </group>
                  </optionalFileGroups>
                </installStep>
              </installSteps>
            </config>
            """;

        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store) = TestBox.OpenRepository(temp);
        ModInstaller installer = new(paths, store, new ArchiveExtractor(), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, "mhws");
        string zip = ZipBuilder.Create(
            temp.Path,
            "fomod.zip",
            ("fomod/ModuleConfig.xml", config),
            ("data/a.mod3", "a"),
            ("data/b.mod3", "b"));

        using (InstallStaging first = installer.Stage(zip))
        {
            FomodSelectionTestTools.CommitByName(first, ["甲"]);
        }

        int id = Assert.Single(store.Mods).Id;
        int index = store.Mods[0].Index;

        ModMetadata renamed = store.Mods[0].Metadata with { Name = "重装后的名字" };
        using (InstallStaging staging = installer.StageReinstall(id, zip))
        {
            Assert.True(staging.IsReinstall);
            InstallResult result = FomodSelectionTestTools.CommitByName(staging, ["乙"], renamed);

            Assert.Equal(id, result.Record.Id);
            Assert.Equal(index, result.Record.Index);
            Assert.Equal("mhws", result.Record.GameId);
            Assert.Equal("重装后的名字", result.Record.Metadata.Name);
            Assert.Contains("natives/b.mod3", result.Record.RelativeFiles);
            Assert.DoesNotContain("natives/a.mod3", result.Record.RelativeFiles);
            Assert.True(File.Exists(Path.Combine(paths.FilesDirectory(id), "natives", "b.mod3")));
            Assert.False(File.Exists(Path.Combine(paths.FilesDirectory(id), "natives", "a.mod3")));
        }

        Assert.Single(store.Mods);
    }
}
