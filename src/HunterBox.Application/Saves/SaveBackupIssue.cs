namespace HunterBox.Application.Saves;

/// <summary>存档备份校验问题类型。</summary>
public enum SaveBackupIssueKind
{
    /// <summary>哈希清单存在但无法读取/解析。</summary>
    ManifestUnreadable,

    /// <summary>哈希清单超过读取上限，按不可读处理，但原因与损坏/版本不支持不同。</summary>
    ManifestTooLarge,

    /// <summary>清单里的路径非法（空、绝对路径或含 <c>..</c>）。</summary>
    InvalidPath,

    /// <summary>清单里有、备份目录里缺失的文件。</summary>
    MissingFile,

    /// <summary>文件大小与清单不一致。</summary>
    SizeMismatch,

    /// <summary>文件哈希与清单不一致。</summary>
    HashMismatch,

    /// <summary>备份目录里存在哈希清单之外的文件（来源不明，不能静默还原）。</summary>
    UnexpectedFile,

    /// <summary>覆盖当前存档后，目标目录里缺失的文件。</summary>
    PostCopyMissingFile,

    /// <summary>覆盖当前存档后，目标文件的大小/哈希与清单不一致。</summary>
    PostCopyMismatch,
}

/// <summary>
/// 一条结构化的存档校验问题：只描述事实与路径，用户文案由表示层按
/// <see cref="Kind"/> 映射（A-16）。
/// </summary>
/// <param name="Kind">问题类型。</param>
/// <param name="RelativePath">备份内相对路径；清单本身不可读时为空串。</param>
public sealed record SaveBackupIssue(SaveBackupIssueKind Kind, string RelativePath);
