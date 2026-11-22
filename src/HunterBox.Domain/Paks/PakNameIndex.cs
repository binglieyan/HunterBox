namespace HunterBox.Domain.Paks;

/// <summary>
/// pak 条目哈希 → 文件路径的对照表。
/// </summary>
/// <remarks>
/// RE Engine 的 pak 只存哈希不存路径，必须靠外部清单（<c>file.list</c>）才能还原文件名。
/// 键的构造见引擎路径哈希约定：高 32 位是 <c>hash(大写路径)</c>、
/// 低 32 位是 <c>hash(调用方传入的路径)</c>，引擎要求传入小写路径（调用方负责先归一为小写）；
/// 本索引只支持按哈希查路径，没有反向查询。
/// </remarks>
public sealed class PakNameIndex
{
    private readonly Dictionary<ulong, string> _map = [];

    /// <summary>已收录的路径数量。</summary>
    public int Count => _map.Count;

    /// <summary>加入一条路径。</summary>
    /// <param name="hash">组合哈希（由调用方按引擎规则算好）。</param>
    /// <param name="path">对应的相对路径。</param>
    /// <returns>该哈希此前不存在时返回 <see langword="true"/>。</returns>
    public bool Add(ulong hash, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return _map.TryAdd(hash, path);
    }

    /// <summary>按组合哈希查路径（返回可空值，不遵循 bool + out 的 Try 模式）。</summary>
    /// <param name="hash">组合哈希。</param>
    /// <returns>找到的路径；不存在时为 <see langword="null"/>。</returns>
    public string? TryResolve(ulong hash) => _map.TryGetValue(hash, out string? path) ? path : null;
}
