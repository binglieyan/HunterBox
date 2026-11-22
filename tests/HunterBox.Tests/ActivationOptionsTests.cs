using HunterBox.Application.Activation;
using HunterBox.Domain.Layout;
using HunterBox.Infrastructure.IO;
using HunterBox.Infrastructure.Paks;
using HunterBox.Infrastructure.Storage;

namespace HunterBox.Tests;

/// <summary>开关与配套依赖不一致时必须在构造期失败，不能静默关闭能力。</summary>
public class ActivationOptionsTests
{
    [Fact]
    public void Constructor_CheckPakInternalFilesWithoutReader_Throws()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store) = TestBox.OpenRepository(temp);

        ArgumentException ex = Assert.Throws<ArgumentException>(() => new ModActivationService(
            paths,
            store,
            temp.Dir("game"),
            TestGames.Wilds,
            new FileContentFingerprint(),
            new ActivationOptions
            {
                FileSystem = SystemFileSystem.Instance,
                CheckPakInternalFiles = true,
            }));

        Assert.Contains("PakReader", ex.Message, StringComparison.Ordinal);
    }
}
