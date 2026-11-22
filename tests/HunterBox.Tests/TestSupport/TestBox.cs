using HunterBox.Domain.Games;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;
using HunterBox.Infrastructure.Storage;

namespace HunterBox.Tests;

/// <summary>只含目录布局的测试盒：Box 根目录 + 游戏根目录。</summary>
internal sealed record TestBoxLayout(BoxPaths Paths, string GameRoot)
{
    /// <summary>游戏根目录内的目标文件绝对路径（入参用 <c>/</c> 分隔）。</summary>
    public string GameFile(string relativePath) =>
        Path.Combine(GameRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
}

/// <summary>带存储实现的测试盒：Box 布局 + 当前游戏配置 + MOD 存储。</summary>
internal sealed record TestBox<TStore>(BoxPaths Paths, string GameRoot, GameProfile Game, TStore Store)
    where TStore : IModStore
{
    /// <summary>游戏根目录内的目标文件绝对路径（入参用 <c>/</c> 分隔）。</summary>
    public string GameFile(string relativePath) =>
        Path.Combine(GameRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>MOD 源文件绝对路径（自动创建父目录）。</summary>
    public string SourceFile(int modId, string relativePath)
    {
        string path = Path.Combine(
            Paths.FilesDirectory(modId),
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return path;
    }
}

/// <summary>
/// A-08：测试盒装配的共享入口，统一「临时目录 → BoxPaths → game 根目录 → 索引仓库」的重复样板。
/// 特殊装配（自定义存储、额外目录）可在工厂之外自行补充。
/// </summary>
internal static class TestBox
{
    /// <summary>只装配目录布局（不加载索引仓库，适用于直接构造挂载/编号服务的测试）。</summary>
    public static TestBoxLayout Layout(TempDir temp) => new(new BoxPaths(temp.Path), temp.Dir("game"));

    /// <summary>只装配 Box 目录与真实索引仓库（不创建游戏根目录）。</summary>
    public static (BoxPaths Paths, ModRepository Store) OpenRepository(TempDir temp)
    {
        BoxPaths paths = new(temp.Path);
        return (paths, ModRepository.Load(paths));
    }

    /// <summary>装配完整测试盒：目录布局 + 真实 <see cref="ModRepository"/>。</summary>
    public static TestBox<ModRepository> Create(TempDir temp, GameProfile? game = null) =>
        Create(temp, static paths => ModRepository.Load(paths), game);

    /// <summary>装配完整测试盒，但存储由 <paramref name="storeFactory"/> 提供（fake / 计数包装）。</summary>
    public static TestBox<TStore> Create<TStore>(
        TempDir temp,
        Func<BoxPaths, TStore> storeFactory,
        GameProfile? game = null)
        where TStore : IModStore
    {
        TestBoxLayout layout = Layout(temp);
        return new TestBox<TStore>(layout.Paths, layout.GameRoot, game ?? TestGames.Wilds, storeFactory(layout.Paths));
    }
}
