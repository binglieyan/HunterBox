namespace HunterBox.Domain.Paks;

/// <summary>某款游戏的 pak 放置配置。</summary>
/// <param name="PatchPrefix">patch 文件名前缀，例如 <c>re_chunk_000.pak.patch_</c>。</param>
/// <param name="PatchNumberFloor">
/// 编号起点下界：实际起点 = max(该下界, 已有最大 patch 号) + 1；下界始终生效，不会因扫描到已有 patch 而失效。
/// </param>
/// <param name="Mode">命名策略：编号或保留原名。</param>
/// <remarks>
/// 只有使用 REFramework 新版 <c>pak_mods</c> 目录的游戏（荒野 / 崛起）才需要本配置。
/// </remarks>
public sealed record PakProfile(string PatchPrefix, int PatchNumberFloor, PakNamingMode Mode);
