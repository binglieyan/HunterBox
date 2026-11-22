namespace HunterBox.Application.Saves;

/// <summary>备份哈希清单里的一条文件记录（与具体序列化格式无关）。</summary>
/// <param name="Path">相对备份目录的路径（<c>/</c> 分隔）。</param>
/// <param name="Size">文件字节数。</param>
/// <param name="Sha256">SHA-256（大写十六进制）。</param>
public sealed record BackupManifestEntry(string Path, long Size, string Sha256);
