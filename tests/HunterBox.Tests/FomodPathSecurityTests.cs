using HunterBox.Domain.Mods;

namespace HunterBox.Tests;

/// <summary>
/// FOMOD source/destination 路径安全。
/// 解析阶段必须拒绝绝对路径（含盘符、UNC、前导分隔符）、<c>..</c> 段与空路径，
/// 跳过该映射并写入 <see cref="FomodConfig.Warnings"/>，不得静默。
/// </summary>
public class FomodPathSecurityTests
{
    private static string Config(string? source, string? destination) =>
        MultiFileConfig([(source, destination)]);

    private static string MultiFileConfig(params (string? Source, string? Destination)[] files)
    {
        string fileElements = string.Join(
            Environment.NewLine,
            files.Select(file =>
            {
                string source = file.Source is null ? string.Empty : $" source=\"{file.Source}\"";
                string destination = file.Destination is null ? string.Empty : $" destination=\"{file.Destination}\"";
                return $"<file{source}{destination} />";
            }));

        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <config>
              <moduleName>路径安全测试</moduleName>
              <installSteps>
                <installStep name="主体">
                  <optionalFileGroups>
                    <group name="文件" type="SelectAny">
                      <plugins>
                        <plugin name="测试插件">
                          <files>
                            {fileElements}
                          </files>
                        </plugin>
                      </plugins>
                    </group>
                  </optionalFileGroups>
                </installStep>
              </installSteps>
            </config>
            """;
    }

    private static FomodPlugin SinglePlugin(FomodConfig config) =>
        Assert.Single(Assert.Single(config.Groups).Plugins);

    [Theory]
    [InlineData("../../x")]
    [InlineData(@"..\..\x")]
    [InlineData("a/../../b")]
    [InlineData(@"..\..\..\windows\system32\x.dll")]
    [InlineData("..")]
    [InlineData("a/b/..")]
    [InlineData(@"C:\Windows\x")]
    [InlineData("C:/Windows/x")]
    [InlineData("C:relative")]
    [InlineData(@"\\server\share\x")]
    [InlineData("//server/share/x")]
    [InlineData("/etc/x")]
    [InlineData(@"\data\x")]
    [InlineData(".")]
    [InlineData("./.")]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_RejectsUnsafeSource_WithWarning(string source)
    {
        FomodConfig config = FomodParser.Parse(Config(source, "natives/stm/safe.mod3"))!;

        Assert.NotNull(config);
        Assert.Empty(SinglePlugin(config).Files);
        Assert.NotEmpty(config.Warnings);
        Assert.Contains(config.Warnings, w => w.Contains("source"));
    }

    [Theory]
    [InlineData("../../x")]
    [InlineData(@"..\..\x")]
    [InlineData("a/../../b")]
    [InlineData(@"C:\Windows\x")]
    [InlineData("C:/Windows/x")]
    [InlineData(@"\\server\share\x")]
    [InlineData("//server/share/x")]
    [InlineData("/etc/x")]
    [InlineData(@"\data\x")]
    [InlineData(".")]
    [InlineData("./.")]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_RejectsUnsafeDestination_WithWarning(string destination)
    {
        FomodConfig config = FomodParser.Parse(Config("data/safe.mod3", destination))!;

        Assert.NotNull(config);
        Assert.Empty(SinglePlugin(config).Files);
        Assert.NotEmpty(config.Warnings);
        Assert.Contains(config.Warnings, w => w.Contains("destination"));
    }

    [Theory]
    [InlineData("../../x", "..")]
    [InlineData("a/../../b", "..")]
    [InlineData(@"C:\Windows\x", "绝对路径")]
    [InlineData("/etc/x", "绝对路径")]
    [InlineData(@"\\server\share\x", "绝对路径")]
    [InlineData(@"\data\x", "绝对路径")]
    [InlineData("", "空")]
    public void Parse_UnsafeWarning_ContainsFieldAndReason(string source, string expectedReason)
    {
        FomodConfig config = FomodParser.Parse(Config(source, "natives/x"))!;

        Assert.Contains(config.Warnings, w => w.Contains("source") && w.Contains(expectedReason));
    }

    [Fact]
    public void Parse_MissingSourceAttribute_IsSkippedWithWarning()
    {
        FomodConfig config = FomodParser.Parse(Config(null, "natives/x"))!;

        Assert.Empty(SinglePlugin(config).Files);
        Assert.Contains(config.Warnings, w => w.Contains("source"));
    }

    [Fact]
    public void Parse_MissingDestinationAttribute_IsSkippedWithWarning()
    {
        FomodConfig config = FomodParser.Parse(Config("data/x", null))!;

        Assert.Empty(SinglePlugin(config).Files);
        Assert.Contains(config.Warnings, w => w.Contains("destination"));
    }

    [Fact]
    public void Parse_SkipsOnlyUnsafeMapping_KeepsSafeOne()
    {
        string xml = MultiFileConfig([
            ("../../evil.dll", "natives/evil.dll"),
            (@"data\good.mod3", "natives/good.mod3"),
        ]);

        FomodConfig config = FomodParser.Parse(xml)!;

        FomodFileMapping mapping = Assert.Single(SinglePlugin(config).Files);
        Assert.Equal("data/good.mod3", mapping.Source);
        Assert.Equal("natives/good.mod3", mapping.Destination);
        Assert.Single(config.Warnings);
        Assert.Contains("source", config.Warnings[0]);
    }

    [Fact]
    public void Parse_BothPathsUnsafe_RecordsTwoWarnings()
    {
        FomodConfig config = FomodParser.Parse(Config("../../evil.dll", @"C:\Windows\evil.dll"))!;

        Assert.Empty(SinglePlugin(config).Files);
        Assert.Equal(2, config.Warnings.Count);
        Assert.Contains(config.Warnings, w => w.Contains("source"));
        Assert.Contains(config.Warnings, w => w.Contains("destination"));
    }

    [Fact]
    public void Parse_AllMappingsUnsafe_StillReturnsConfigWithWarnings()
    {
        FomodConfig config = FomodParser.Parse(Config("/etc/passwd", @"\\server\share\x"))!;

        Assert.NotNull(config);
        Assert.Empty(SinglePlugin(config).Files);
        Assert.NotEmpty(config.Warnings);
    }

    [Theory]
    [InlineData(@"data\body\normal.mod3", "natives/stm/body.mod3", "data/body/normal.mod3", "natives/stm/body.mod3")]
    [InlineData("DATA/Body/Normal.MOD3", "Natives/STM/Body.MOD3", "data/body/normal.mod3", "natives/stm/body.mod3")]
    [InlineData("./data/x", "./natives/x", "data/x", "natives/x")]
    [InlineData(@"  data\body\a.bin  ", "  natives/stm/a.bin  ", "data/body/a.bin", "natives/stm/a.bin")]
    [InlineData("data//body///a.bin", "natives//a.bin", "data/body/a.bin", "natives/a.bin")]
    public void Parse_AcceptsSafeRelativePaths_WithoutWarnings(
        string source,
        string destination,
        string expectedSource,
        string expectedDestination)
    {
        FomodConfig config = FomodParser.Parse(Config(source, destination))!;

        FomodFileMapping mapping = Assert.Single(SinglePlugin(config).Files);
        Assert.Equal(expectedSource, mapping.Source);
        Assert.Equal(expectedDestination, mapping.Destination);
        Assert.Empty(config.Warnings);
    }

    [Fact]
    public void NewConfig_DefaultsToEmptyWarnings()
    {
        FomodConfig config = new("测试", []);

        Assert.Empty(config.Warnings);
    }
}
