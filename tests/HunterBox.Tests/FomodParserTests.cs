using HunterBox.Domain.Mods;

namespace HunterBox.Tests;

public class FomodParserTests
{
    private const string Sample = """
        <?xml version="1.0" encoding="utf-8"?>
        <config>
          <moduleName>测试组件包</moduleName>
          <installSteps>
            <installStep name="主体">
              <optionalFileGroups>
                <group name="体型" type="SelectExactlyOne">
                  <plugins>
                    <plugin name="普通体型">
                      <description>默认</description>
                      <files>
                        <file source="data\body\normal.mod3" destination="natives/stm/body.mod3" />
                      </files>
                    </plugin>
                    <plugin name="大体型">
                      <files>
                        <file source="data\body\large.mod3" destination="natives/stm/body.mod3" />
                      </files>
                    </plugin>
                  </plugins>
                </group>
                <group name="附加" type="SelectAny">
                  <plugins>
                    <plugin name="声音包">
                      <files>
                        <file source="data/sound/a.sbnk" destination="natives/stm/sound/a.sbnk" />
                      </files>
                    </plugin>
                  </plugins>
                </group>
              </optionalFileGroups>
            </installStep>
          </installSteps>
        </config>
        """;

    [Fact]
    public void Parse_ReadsNameGroupsAndPlugins()
    {
        FomodConfig? config = FomodParser.Parse(Sample);

        Assert.NotNull(config);
        Assert.Equal("测试组件包", config!.ModuleName);
        Assert.Equal(2, config.Groups.Count);

        FomodGroup body = config.Groups[0];
        Assert.Equal("体型", body.Name);
        Assert.Equal(FomodGroupType.SelectExactlyOne, body.Type);
        Assert.Equal(2, body.Plugins.Count);
        Assert.Equal("普通体型", body.Plugins[0].Name);
        Assert.Equal("默认", body.Plugins[0].Description);
    }

    [Fact]
    public void Parse_NormalizesFileMappings()
    {
        FomodConfig config = FomodParser.Parse(Sample)!;

        FomodFileMapping mapping = Assert.Single(config.Groups[0].Plugins[0].Files);
        Assert.Equal("data/body/normal.mod3", mapping.Source);
        Assert.Equal("natives/stm/body.mod3", mapping.Destination);
    }

    [Fact]
    public void Parse_SelectAnyGroup()
    {
        FomodConfig config = FomodParser.Parse(Sample)!;

        Assert.Equal(FomodGroupType.SelectAny, config.Groups[1].Type);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not xml")]
    [InlineData("<config><moduleName>x</moduleName></config>")]
    public void Parse_InvalidOrEmpty_ReturnsNull(string? xml)
    {
        Assert.Null(FomodParser.Parse(xml));
    }
}
