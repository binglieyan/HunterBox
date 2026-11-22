namespace HunterBox.Domain.Models;

/// <summary>模型 ID 改写规则：按「完整模型身份」改写路径里的模型段（Keys 模板替换）。</summary>
/// <remarks>
/// <para>
/// 模型路径不是只有一段 ID：荒野护甲是 <c>ch0X/fid/sid</c> 两段（外加 _prefab 的性别目录与
/// <c>ch0X_FID_SIDN</c> 文件名、骨骼）；崛起/世界还各有目录与部件文件名 token。只替换单个数字段
/// 会生成不存在的目标路径，所以改写的输入/输出统一是 <see cref="ModelIdentity"/>。
/// </para>
/// <para>
/// 具体按游戏的路径规则见 <see cref="ModelPathRules"/>；这里保留历史 API 并做参数校验/分派。
/// 改写只改路径，不修改文件内容；<c>.tex.</c> 贴图路径一律跳过。
/// </para>
/// </remarks>
public static class ModelIdRewriter
{
    /// <summary>校验模型 ID 是否为三位或四位数字（护甲 3 位、武器 4 位）。</summary>
    /// <param name="id">待校验的 ID。</param>
    /// <returns>合法时返回 <see langword="true"/>。</returns>
    public static bool IsValidId(string? id) =>
        id is { Length: 3 or 4 } && id.All(char.IsAsciiDigit);

    /// <summary>按「模型身份段」安全改写路径里的模型 ID（旧接口：只能改同一身份槽位）。</summary>
    /// <remarks>
    /// 只改模型身份所在的位置：护甲/角色路径只改 <c>art/model/character/chXX/</c> 后的第一段，
    /// 武器/物品路径只改 <c>art/model/item/itTT/AA/</c> 后的 <c>BBBB</c> 段，
    /// 避免把同号的 sid/AA 段误改掉。
    /// 但它不会同步改动 sid / AA / 文件名 / 骨骼，所以会产生与目标模型不一致的路径。
    /// 新代码请用 <see cref="TryRewriteIdentityPath"/>。
    /// </remarks>
    /// <param name="path">原始相对路径（<c>/</c> 或 <c>\</c> 分隔均可）。</param>
    /// <param name="fromId">源模型 ID（三位或四位数字）。</param>
    /// <param name="toId">目标模型 ID（位数需与 <paramref name="fromId"/> 相同）。</param>
    /// <param name="rewritten">改写后的路径；未命中时等于 <paramref name="path"/>。</param>
    /// <returns>路径里的模型身份段确实被改写时返回 <see langword="true"/>。</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> 为空，或 ID 不是 3–4 位数字/位数不一致。</exception>
    public static bool TryRewritePath(string path, string fromId, string toId, out string rewritten)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!IsValidId(fromId) || !IsValidId(toId))
        {
            throw new ArgumentException($"模型 ID 必须是 3–4 位数字: {fromId} -> {toId}");
        }

        if (fromId.Length != toId.Length)
        {
            throw new ArgumentException($"源/目标模型 ID 位数必须一致: {fromId} -> {toId}");
        }

        rewritten = path;
        if (string.Equals(fromId, toId, StringComparison.Ordinal))
        {
            return false;
        }

        string[] segments = path.Replace('\\', '/').Split('/');
        int index = FindModelIdSegment(segments);
        if (index >= 0)
        {
            if (!string.Equals(segments[index], fromId, StringComparison.Ordinal))
            {
                return false;
            }
        }
        else
        {
            index = FindUniqueSegment(segments, fromId);
            if (index < 0)
            {
                return false;
            }
        }

        segments[index] = toId;
        rewritten = string.Join('/', segments);
        return true;
    }

    /// <summary>校验源/目标身份可以互相改写（同游戏、同类别、同类型；护甲性别/武器类型不允许换）。</summary>
    /// <param name="from">源模型身份。</param>
    /// <param name="to">目标模型身份。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException">游戏/类别或类型不一致（例如男款护甲换女款、大剑换弓）。</exception>
    /// <remarks>
    /// 荒野武器比较的是路径类型（<see cref="ModelIdentity.PathType"/>）而不是 14/10 的类型编号：
    /// 猎虫（14）与操虫棍（10）共用 <c>it10</c> 路径，两者改写的是同一组路径，不应互相拒绝。
    /// </remarks>
    public static void ValidateIdentityPair(ModelIdentity from, ModelIdentity to) =>
        ModelPathRules.EnsureCompatible(from, to);

    /// <summary>
    /// 按完整模型身份改写一条路径（护甲改 <c>fid/sid</c> + 文件名/token，武器改 <c>AA/BBBB</c> + 文件名）。
    /// </summary>
    /// <remarks>
    /// 具体规则按身份所属游戏分派到 <see cref="ModelPathRules.TryRewritePath"/>：
    /// 荒野按 <c>ch0X/fid/sid</c>、<c>_prefab</c>、骨骼与武器文件名整体替换；
    /// 崛起按 <c>player/mod</c>、<c>weapon</c>、prefab、骨骼 token 替换；
    /// 世界按 <c>nativepc/pl/*_equip</c> 与 <c>nativepc/wp</c> 的代码/部件 token 替换。
    /// 只改属于该身份的路径，不碰无关数字段；<c>.tex.</c> 贴图路径不改名
    /// （规则：模型文件内容不重写，贴图留在原路径才不会白模）。
    /// 无法识别或目标身份缺少对应路径角色时返回 <see langword="false"/> 且原样返回，不静默乱改。
    /// </remarks>
    /// <param name="path">原始相对路径（<c>/</c> 或 <c>\</c> 分隔均可）。</param>
    /// <param name="from">源模型身份。</param>
    /// <param name="to">目标模型身份（须与 <paramref name="from"/> 同游戏同类别同类型）。</param>
    /// <param name="rewritten">改写后的路径；未命中时等于 <paramref name="path"/>。</param>
    /// <returns>路径确实被改写时返回 <see langword="true"/>。</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> 为空，或源/目标身份游戏/类别/类型不一致。</exception>
    /// <exception cref="ArgumentNullException">任一身份为 <see langword="null"/>。</exception>
    public static bool TryRewriteIdentityPath(
        string path,
        ModelIdentity from,
        ModelIdentity to,
        out string rewritten)
    {
        // 路径与身份校验统一由 ModelPathRules.TryRewritePath 执行一次，
        // 这里只做分派，避免每个文件重复校验 3 次；异常契约不变。
        return ModelPathRules.TryRewritePath(path, from, to, out rewritten);
    }

    /// <summary>定位路径里的模型身份段下标；无法识别路径形态时返回 -1。</summary>
    private static int FindModelIdSegment(string[] segments)
    {
        // 角色/护甲：art/model/character/chXX/<模型 ID>/...
        for (int i = 1; i < segments.Length; i++)
        {
            if (IsCharacterGroup(segments[i - 1]) && IsAllDigits(segments[i], 3))
            {
                return i;
            }
        }

        // 武器/物品：art/model/item/itTT/<AA>/<BBBB>/...
        for (int i = 2; i < segments.Length; i++)
        {
            if (IsItemGroup(segments[i - 2]) && IsAllDigits(segments[i - 1], 2) && IsAllDigits(segments[i], 4))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>在无法识别路径形态时，找唯一一个等于 ID 的路径段。</summary>
    private static int FindUniqueSegment(string[] segments, string id)
    {
        int found = -1;
        for (int i = 0; i < segments.Length; i++)
        {
            if (!string.Equals(segments[i], id, StringComparison.Ordinal))
            {
                continue;
            }

            if (found >= 0)
            {
                return -1;
            }

            found = i;
        }

        return found;
    }

    private static bool IsCharacterGroup(string segment) =>
        segment.Length == 4
        && (segment[0] is 'c' or 'C')
        && (segment[1] is 'h' or 'H')
        && char.IsAsciiDigit(segment[2])
        && char.IsAsciiDigit(segment[3]);

    private static bool IsItemGroup(string segment) =>
        segment.Length == 4
        && (segment[0] is 'i' or 'I')
        && (segment[1] is 't' or 'T')
        && char.IsAsciiDigit(segment[2])
        && char.IsAsciiDigit(segment[3]);

    private static bool IsAllDigits(string segment, int length) =>
        segment.Length == length && segment.All(char.IsAsciiDigit);

}
