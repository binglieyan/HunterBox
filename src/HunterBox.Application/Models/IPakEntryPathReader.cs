using HunterBox.Domain.Models;
using HunterBox.Domain.Paks;

namespace HunterBox.Application.Models;

/// <summary>
/// 读取 pak 条目路径的端口：把 RE PAK 的解析/解密/路径还原留给 Infrastructure，
/// Application 的模型识别逻辑只依赖本接口，便于用假实现做不碰磁盘的单元测试。
/// </summary>
public interface IPakEntryPathReader
{
    /// <summary>
    /// 读取一个 pak 的条目路径（用于模型识别）。
    /// </summary>
    /// <param name="pakPath">pak 绝对路径。</param>
    /// <param name="pakName">pak 名（日志/原因用）。</param>
    /// <param name="index">内置 pak 路径清单（hash → 路径）。</param>
    /// <param name="reason">读取失败的原因；成功时为 <see langword="null"/>。</param>
    /// <returns>还原后的条目路径；pak 不存在/过大/无法解析时返回 <see langword="null"/>（TOC 加密的
    /// pak 会先解密再读条目，不以加密为由跳过）。</returns>
    IReadOnlyList<string>? TryReadEntryPaths(
        string pakPath,
        string pakName,
        PakNameIndex index,
        out string? reason);

    /// <summary>加载指定游戏的 pak 路径清单（hash → 路径）索引。</summary>
    /// <param name="game">游戏；<see cref="ModelGame.Unknown"/> 按荒野清单处理（与旧行为一致）。</param>
    /// <returns>路径索引；该作不支持/没有清单时返回空索引。</returns>
    PakNameIndex LoadNameIndex(ModelGame game);
}
