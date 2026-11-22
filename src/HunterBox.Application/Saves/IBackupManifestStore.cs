namespace HunterBox.Application.Saves;

/// <summary>
/// 备份哈希清单的文本编解码端口：Application 只处理结构化条目，
/// 具体格式（当前为 JSON）由 Infrastructure 实现。
/// </summary>
public interface IBackupManifestStore
{
    /// <summary>把清单条目编码成可写入磁盘的文本。</summary>
    /// <param name="createdAt">清单生成时间。</param>
    /// <param name="entries">文件条目；调用方保证非空。</param>
    /// <returns>清单文本。</returns>
    string Serialize(DateTimeOffset createdAt, IReadOnlyList<BackupManifestEntry> entries);

    /// <summary>
    /// 解码清单文本。
    /// </summary>
    /// <param name="json">磁盘上的清单文本。</param>
    /// <returns>
    /// 结构化条目；文本损坏、版本不支持或没有任何有效条目时返回 <see langword="null"/>
    /// （调用方按「清单不可读」处理，不能在还原时当成有效的空清单）。
    /// </returns>
    IReadOnlyList<BackupManifestEntry>? TryDeserialize(string json);
}
