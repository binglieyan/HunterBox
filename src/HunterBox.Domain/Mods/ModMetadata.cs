namespace HunterBox.Domain.Mods;

/// <summary>MOD的可编辑元数据（对应界面里的「MOD信息设置」）。</summary>
/// <remarks>所有字段都可为空：来源不同的包未必带全信息，界面需容忍缺字段。</remarks>
public sealed record ModMetadata
{
    /// <summary>显示名字；为空时界面应回退到「MOD #id」。</summary>
    public string? Name { get; init; }

    /// <summary>分类（模型替换 / 特效 / 功能 等）。</summary>
    public string? Category { get; init; }

    /// <summary>版本号文本，保留原始写法。</summary>
    public string? Version { get; init; }

    /// <summary>作者。</summary>
    public string? Author { get; init; }

    /// <summary>NexusMods 数字 ID；无法解析时为 <see langword="null"/>。</summary>
    public int? NexusId { get; init; }

    /// <summary>MOD 来源页地址（踩蘑菇 / Nexus 等）。</summary>
    public string? HomeUrl { get; init; }

    /// <summary>备注信息，界面列表的「备注信息」列显示它。</summary>
    public string? Description { get; init; }
}
