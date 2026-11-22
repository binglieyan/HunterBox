namespace HunterBox.Domain.Mods;

/// <summary>HunterBox里的一条 MOD 记录（<c>docs/SPEC.md</c> 第 2 节）。</summary>
/// <remarks>
/// <see cref="ModRecord"/> 是聚合根：允许可变（启用状态、排序、元数据编辑），
/// 但不允许改 <see cref="Id"/>；其余决策结构一律用不可变 <c>record</c>。
/// </remarks>
public sealed class ModRecord
{
    /// <summary><see cref="GameId"/> 的存储字段：分配游戏时用整体复制的方式覆盖，不逐个字段重建。</summary>
    private string? _gameId;

    /// <summary>持久 ID：安装时分配、自增且不复用。</summary>
    public int Id { get; init; }

    /// <summary>
    /// 所属游戏的稳定 ID（如 <c>mhws</c> / <c>mhw</c>）；<see langword="null"/> 或空表示
    /// 旧数据尚未归属任何游戏（加载时保留，但不出现在任何游戏列表，也不允许启用/清理）。
    /// </summary>
    /// <remarks>
    /// 多游戏共用同一 <c>Mods/index.json</c>，列表、启用、备份路径与重复安装检测都按本字段隔离。
    /// JSON 里字段名同为 <c>GameId</c>；旧索引没有该字段时反序列化为 <see langword="null"/>。
    /// </remarks>
    public string? GameId
    {
        get => _gameId;
        init => _gameId = value;
    }

    /// <summary>列表展示顺序，从 1 开始；与 <see cref="Id"/> 解耦，可随意重排。</summary>
    public int Index { get; set; }

    /// <summary>是否已启用（文件是否已铺到游戏目录）。</summary>
    public bool Enabled { get; set; }

    /// <summary>可编辑元数据。</summary>
    public ModMetadata Metadata { get; set; } = new();

    /// <summary>安装时判定的包种类。</summary>
    public ModKind Kind { get; set; }

    /// <summary>相对游戏根目录的落点；<see langword="null"/> 表示沿用包内路径。</summary>
    public string? TargetDirectory { get; set; }

    /// <summary>
    /// MOD 内全部文件，相对 <c>files</c> 目录、正斜杠。
    /// </summary>
    /// <remarks>安装时写入小写；重扫/模型改写后可能保留磁盘上的原始大小写，因此所有比较都必须忽略大小写。</remarks>
    public List<string> RelativeFiles { get; set; } = [];

    /// <summary>包内容指纹（文件清单 + 大小），用于阻止重复安装同一个包。</summary>
    public string? PackageFingerprint { get; set; }

    /// <summary>
    /// 安装源路径（原始压缩包或文件夹；安装选项为复制/移动时指向HunterBox <c>Download</c> 里的归档）。
    /// 重装/更新优先用它，缺失再让用户选包。
    /// </summary>
    /// <remarks>JSON 里字段名同为 <c>InstallSource</c>；旧索引没有该字段时反序列化为 <see langword="null"/>。</remarks>
    public string? InstallSource { get; set; }

    /// <summary>安装时间（UTC）；重装时更新为本次安装时间。</summary>
    public DateTimeOffset InstalledAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>界面显示名：优先元数据里的名字，缺失时回退到「MOD #id」。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Metadata.Name) ? $"MOD #{Id}" : Metadata.Name!;

    /// <summary>复制本记录并改到指定游戏与排序号（分配游戏用；其余字段整体复制，避免逐字段漏拷）。</summary>
    /// <remarks>
    /// 用 <c>MemberwiseClone</c> 复制后只覆盖 <see cref="GameId"/> 与 <see cref="Index"/>，
    /// 这样以后新增持久字段会自动被复制，不会被漏掉；记录里的集合/元数据仍与副本共享引用（只换归属，不原地改内容）。
    /// </remarks>
    /// <param name="gameId">目标游戏稳定 ID（会去掉首尾空白）。</param>
    /// <param name="index">在该游戏列表里的顺序号。</param>
    /// <returns>新的记录副本。</returns>
    /// <exception cref="ArgumentException"><paramref name="gameId"/> 为空或空白。</exception>
    public ModRecord WithGame(string gameId, int index)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        ModRecord clone = (ModRecord)MemberwiseClone();
        clone._gameId = gameId.Trim();
        clone.Index = index;
        return clone;
    }

    /// <summary>判断本记录是否属于指定游戏（忽略大小写；未归属记录不属于任何游戏）。</summary>
    /// <param name="gameId">当前游戏稳定 ID。</param>
    /// <returns>归属该游戏时返回 <see langword="true"/>。</returns>
    /// <exception cref="ArgumentException"><paramref name="gameId"/> 为 <see langword="null"/>、空或空白。</exception>
    public bool BelongsTo(string gameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        return !string.IsNullOrWhiteSpace(GameId)
            && string.Equals(GameId.Trim(), gameId.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
