using System.IO.Compression;
using System.Reflection;
using HunterBox.Domain.Models;
using HunterBox.Domain.Paks;

namespace HunterBox.Infrastructure.Paks;

/// <summary>
/// 内置的游戏路径清单（按 gzip 压缩后随程序集分发）。
/// </summary>
/// <remarks>
/// 清单只含模型路径，用于把 pak 条目哈希还原成路径，从而支持按路径改模型 ID。
/// 荒野与崛起各一份清单；世界不支持 pak 内改写，返回空索引。
/// 用户也可以在设置里指定自己的 <c>file.list</c> 覆盖内置清单。
/// </remarks>
public static class EmbeddedPakNameIndex
{
    private const string WildsResource = "HunterBox.Infrastructure.Paks.FileLists.mhws.models.list.gz";
    private const string RiseResource = "HunterBox.Infrastructure.Paks.FileLists.mhr.models.list.gz";

    private static readonly Lazy<IReadOnlyList<string>> WildsPathList = new(() => LoadLines(WildsResource));
    private static readonly Lazy<IReadOnlyList<string>> RisePathList = new(() => LoadLines(RiseResource));
    private static readonly Lazy<PakNameIndex> WildsIndex = new(() => PakPathHasher.BuildIndex(WildsPathList.Value));
    private static readonly Lazy<PakNameIndex> RiseIndex = new(() => PakPathHasher.BuildIndex(RisePathList.Value));

    /// <summary>加载荒野内置清单构建索引。</summary>
    /// <returns>构建好的索引；资源缺失时返回空索引。</returns>
    public static PakNameIndex LoadWilds() => WildsIndex.Value;

    /// <summary>加载崛起内置清单构建索引。</summary>
    /// <returns>构建好的索引；资源缺失时返回空索引。</returns>
    public static PakNameIndex LoadRise() => RiseIndex.Value;

    /// <summary>按游戏加载内置清单索引（世界返回空索引，因为世界 pak 不支持内改路径）。</summary>
    /// <param name="game">游戏。</param>
    /// <returns>构建好的索引；该作没有清单时返回空索引。</returns>
    public static PakNameIndex Load(ModelGame game) => game switch
    {
        ModelGame.Wilds => LoadWilds(),
        ModelGame.Rise => LoadRise(),
        _ => new PakNameIndex(),
    };

    /// <summary>读取荒野内置清单里的全部模型路径（保留原始大小写）。</summary>
    /// <returns>路径列表；资源缺失时为空。</returns>
    /// <remarks>模型编辑器用它判断候选目标是否有 <c>.chain</c> 文件（(chain) 标记），不要修改返回的列表。</remarks>
    public static IReadOnlyList<string> LoadWildsPaths() => WildsPathList.Value;

    private static IReadOnlyList<string> LoadLines(string resourceName)
    {
        using Stream? resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (resource is null)
        {
            return [];
        }

        using GZipStream gzip = new(resource, CompressionMode.Decompress);
        using StreamReader reader = new(gzip);
        return [.. ReadLines(reader)];
    }

    private static IEnumerable<string> ReadLines(TextReader reader)
    {
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                yield return line;
            }
        }
    }
}
