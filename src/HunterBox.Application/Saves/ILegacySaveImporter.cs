using HunterBox.Domain.Games;

namespace HunterBox.Application.Saves;

/// <summary>一条旧存档 zip 存档备份。</summary>
/// <param name="FilePath">旧 zip的绝对路径。</param>
/// <param name="FileName">zip 文件名（含扩展名，可能带「解压码xxx」提示）。</param>
/// <param name="LastWriteTime">zip 最后修改时间（用作新备份的时间戳）。</param>
/// <param name="SizeBytes">zip 文件大小（字节）。</param>
public sealed record LegacySaveArchive(
    string FilePath,
    string FileName,
    DateTimeOffset LastWriteTime,
    long SizeBytes);

/// <summary>
/// 旧存档备份导入端口（新格式为目录备份，旧 <c>Saves_&lt;appid&gt;/*.zip</c> 通过本端口导入）。
/// </summary>
/// <remarks>实现位于 Infrastructure（SharpCompress + 文件系统）；只读取旧 zip，不删除、不修改源文件。</remarks>
public interface ILegacySaveImporter
{
    /// <summary>本存放存档 zip的目录。</summary>
    /// <param name="game">当前游戏。</param>
    /// <returns><c>&lt;HunterBox根&gt;/Saves_&lt;appid&gt;</c>；崛起（1446780）为 <c>&lt;HunterBox根&gt;/Saves</c>。</returns>
    string LegacySaveDirectory(GameProfile game);

    /// <summary>列出旧存档 zip 备份，按修改时间倒序。</summary>
    /// <param name="game">当前游戏。</param>
    /// <returns>旧 zip 列表；目录不存在或没有 zip 时为空。</returns>
    IReadOnlyList<LegacySaveArchive> List(GameProfile game);

    /// <summary>把一条旧存档 zip 导入为新的目录备份（解压到 <c>Saves/&lt;gameId&gt;/&lt;时间戳&gt;/</c>）。</summary>
    /// <param name="game">当前游戏。</param>
    /// <param name="archive">要导入的旧 zip。</param>
    /// <param name="cancellationToken">取消令牌：条目边界与复制分块处检查，取消时清理本次半成品备份。</param>
    /// <returns>导入后出现在 <see cref="SaveBackupService.ListAsync"/> 里的新备份。</returns>
    /// <exception cref="FileNotFoundException">zip 文件不存在。</exception>
    /// <exception cref="InvalidDataException">zip 损坏、含越界路径或需要密码但无法解出。</exception>
    /// <exception cref="IOException">解压/写盘失败。</exception>
    /// <exception cref="OperationCanceledException">导入已取消（不留下半成品备份）。</exception>
    SaveBackup Import(GameProfile game, LegacySaveArchive archive, CancellationToken cancellationToken = default);
}
