using HunterBox.Domain.Models;

namespace HunterBox.Tests;

public class ModelPathRecognizerTests
{
    [Theory]
    [InlineData("natives/stm/art/model/character/ch03/012/body.mod3", "ch03/012")]
    [InlineData("natives/STM/Art/Model/Character/CH03/012/body.mesh.24111", "ch03/012")]
    [InlineData(@"natives\stm\art\model\character\ch01\500\x.mdf2.45", "ch01/500")]
    [InlineData("natives/STM/Art/Model/Item/it03/00/0017/it0300_0017_0.mesh.241111606", "it03/00/0017")]
    public void FindModelIds_RecognizesCharacterAndItemPaths(string path, string expected)
    {
        Assert.Equal([expected], ModelPathRecognizer.FindModelIds([path]));
    }

    [Fact]
    public void FindModelIds_DeduplicatesAndKeepsOrder()
    {
        string[] paths =
        [
            "natives/stm/art/model/character/ch03/012/a.mod3",
            "natives/stm/art/model/character/ch03/012/b.mod3",
            "natives/stm/art/model/character/ch03/013/c.mod3",
        ];

        Assert.Equal(["ch03/012", "ch03/013"], ModelPathRecognizer.FindModelIds(paths));
    }

    [Fact]
    public void FindModelIds_IgnoresNonModelPaths()
    {
        string[] paths =
        [
            "natives/stm/art/model/character/ch03/readme.txt",
            "natives/stm/sound/bgm.bnk",
            "reframework/autorun/x.lua",
        ];

        Assert.Empty(ModelPathRecognizer.FindModelIds(paths));
        Assert.Empty(ModelPathRecognizer.FindIdentities(paths));
    }
}
