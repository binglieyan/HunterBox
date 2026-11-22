using System.Buffers.Binary;
using HunterBox.Domain.Paks;
using HunterBox.Application.Paks;
using HunterBox.Infrastructure.IO;

namespace HunterBox.Infrastructure.Paks;

/// <summary>把改动后的内容写回 pak（仅支持等长原地替换）。</summary>
/// <remarks>
/// <para>
/// 只处理「未加密 + 未压缩」的条目：等长替换不改变偏移与条目表布局，风险最低。
/// 但 v4 的条目 checksum 是内容摘要，内容变了必须同步重算；v2 条目没有 checksum 字段，按布局跳过。
/// 需要变长或改条目数量时应当整体重打包。
/// </para>
/// <para>
/// 写入是事务化的：先把原 pak 复制到同目录临时副本，数据与新 checksum 都写进副本并落盘，
/// 最后用同卷 rename 一次替换原文件；任一步失败原 pak 保持原样，只清理临时文件。
/// <c>.bak</c> 只在首次写回前生成；后续替换保留最初原始备份。
/// </para>
/// <para>
/// TOC 加密（feature 8/24）时无法只写明文 checksum 字段（写进去会与密文 TOC 混在一起），
/// 抛出 <see cref="NotSupportedException"/>，调用方应改用整包解包→重打包。
/// </para>
/// </remarks>
public sealed class RePakWriter
{
    /// <summary>原地替换一个条目的内容。</summary>
    /// <remarks>
    /// 旧的内容等长替换接口，仅为兼容/测试保留：生产代码的模型改写走
    /// <c>PakModelRewriteService.RewritePaths</c>（只改 TOC 哈希，不复制整包）。
    /// </remarks>
    /// <param name="pakPath">pak 文件路径。</param>
    /// <param name="entry">要替换的条目（必须来自同一个 pak）。</param>
    /// <param name="data">新内容，长度必须与条目原始大小一致。</param>
    /// <param name="backup">
    /// 为 <see langword="true"/> 且 <c>&lt;pak&gt;.bak</c> 尚不存在时，先生成整包前像；
    /// 为 <see langword="false"/> 时不动已有备份。已有 <c>.bak</c> 时始终保留最初备份。
    /// </param>
    /// <exception cref="NotSupportedException">条目加密、压缩、长度不一致，或 TOC 已加密。</exception>
    /// <exception cref="InvalidDataException">条目越界或不在条目表中。</exception>
    public void ReplaceEntry(string pakPath, PakEntryInfo entry, byte[] data, bool backup = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pakPath);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(data);

        string targetPath = Path.GetFullPath(pakPath);
        string targetDirectory = Path.GetDirectoryName(targetPath)
            ?? throw new ArgumentException("无法确定 pak 所在目录", nameof(pakPath));
        string fileName = Path.GetFileName(targetPath);

        // 临时文件与目标同目录（同卷）：rename 才是原子的；跨卷 Move 会退化成复制。
        string stagedPath = Path.Combine(targetDirectory, $".{fileName}.{Guid.NewGuid():N}.tmp");
        string? backupTempPath = null;
        try
        {
            using (FileStream source = new(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                // 以 pak 自己的 TOC 条目为准：调用方传入的 entry 可能来自另一个 pak，
                // 不能只信它的 offset/size（v2 尤其危险，会覆盖相邻条目数据）。
                // TOC 只读一次，同时定位 checksum 字段，避免同一张表解析两遍（P-20）。
                (PakEntryInfo current, long checksumFieldOffset) = ResolveEntryLocation(source, entry);

                if (current.EncryptionType != 0)
                {
                    throw new NotSupportedException("不支持改写加密条目");
                }

                if (current.CompressionType != 0)
                {
                    throw new NotSupportedException("不支持改写压缩条目（需要整体重打包）");
                }

                long size = current.CompressedSize > 0 ? current.CompressedSize : current.DecompressedSize;
                if (data.LongLength != size)
                {
                    throw new NotSupportedException($"长度不一致：{data.LongLength} != {size}（需要整体重打包）");
                }

                // 第一步只读校验：条目必须在文件范围内，且能在条目表里定位到 checksum 字段。
                // 校验全部在「动笔」之前完成，失败时原 pak 与 .bak 都不会被碰。
                if (current.Offset < 0 || size < 0 || current.Offset > source.Length - size)
                {
                    throw new InvalidDataException("条目超出 pak 文件范围");
                }

                // 第二步：在临时副本上完成数据 + checksum 两次写并落盘；原 pak 全程只读。
                using (FileStream staged = new(stagedPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    source.Position = 0;
                    source.CopyTo(staged);

                    staged.Seek(current.Offset, SeekOrigin.Begin);
                    staged.Write(data);

                    if (checksumFieldOffset >= 0)
                    {
                        Span<byte> checksumBytes = stackalloc byte[8];
                        BinaryPrimitives.WriteUInt64LittleEndian(checksumBytes, PakChecksum.Compute(data));
                        staged.Seek(checksumFieldOffset, SeekOrigin.Begin);
                        staged.Write(checksumBytes);
                    }

                    staged.Flush(flushToDisk: true);
                }

                // 第三步：只在首次写回前生成原始 .bak；后续替换不覆盖最初备份。
                // 内容替换会改数据区，必须保留整包前像；只改 TOC 的改名改用 .toc.bak 前像。
                if (backup)
                {
                    string backupPath = targetPath + ".bak";
                    if (!File.Exists(backupPath))
                    {
                        backupTempPath = Path.Combine(
                            targetDirectory,
                            $".{fileName}.bak.{Guid.NewGuid():N}.tmp");
                        using (FileStream backupTemp = new(
                            backupTempPath,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None))
                        {
                            source.Position = 0;
                            source.CopyTo(backupTemp);
                            backupTemp.Flush(flushToDisk: true);
                        }

                        File.Move(backupTempPath, backupPath, overwrite: true);
                        backupTempPath = null;
                    }
                }
            }

            // 第四步：唯一的可见提交点——把已包含新数据 + 新 checksum 的副本原子替换原 pak。
            File.Move(stagedPath, targetPath, overwrite: true);
        }
        catch
        {
            // 走不到提交点时原 pak 未被改动：清掉临时副本 / 临时备份即可，异常原样上抛便于诊断。
            SystemFileSystem.Instance.TryDeleteFile(backupTempPath);
            SystemFileSystem.Instance.TryDeleteFile(stagedPath);
            throw;
        }
    }

    /// <summary>
    /// 以 pak 自己的条目表为准解析要写入的条目，并同时定位它的 checksum 字段（一次解析，P-20）。
    /// </summary>
    /// <remarks>
    /// 调用方传入的 entry 必须能在该 pak 的 TOC 里找到，之后一律使用 TOC 里的 offset / 大小 / 属性，
    /// 避免错配 entry 覆盖相邻数据（N-22）。
    /// </remarks>
    /// <param name="stream">已打开的 pak 数据流（只读）。</param>
    /// <param name="requested">调用方传入的条目。</param>
    /// <returns>TOC 中的权威条目，以及 checksum 字段的绝对偏移（v2 为 -1）。</returns>
    /// <exception cref="NotSupportedException">TOC 已加密。</exception>
    /// <exception cref="InvalidDataException">pak 结构不合法或条目不在 TOC 中。</exception>
    private static (PakEntryInfo Entry, long ChecksumFieldOffset) ResolveEntryLocation(
        FileStream stream,
        PakEntryInfo requested)
    {
        PakPlainHeader header = PakPlainToc.ReadHeader(stream);
        if (PakFeature.IsTocEncrypted(header.Feature))
        {
            throw new NotSupportedException(
                $"pak 的条目表已加密（feature={header.Feature}），不能只改条目内容并重算 checksum；请改用整包解包→重打包");
        }

        byte[] toc = PakPlainToc.ReadToc(stream, header);
        int entrySize = PakEntryLayout.EntrySize(header.Major);
        int checksumInEntry = PakEntryLayout.ChecksumFieldOffset(header.Major);
        PakEntryInfo? byHash = null;
        long byHashChecksumOffset = -1;
        int hashCount = 0;
        for (int index = 0; index < header.Count; index++)
        {
            PakEntryInfo candidate = PakEntryLayout.Read(
                header.Major,
                toc.AsSpan(index * entrySize, entrySize));
            if (candidate.Hash != requested.Hash)
            {
                continue;
            }

            hashCount++;
            byHash = candidate;
            byHashChecksumOffset = checksumInEntry < 0
                ? -1
                : PakPlainToc.HeaderSize + ((long)index * entrySize) + checksumInEntry;

            if (candidate.Offset == requested.Offset)
            {
                // 正常路径：调用方条目就来自同一个 pak，hash + offset 精确匹配。
                return (candidate, byHashChecksumOffset);
            }
        }

        if (hashCount == 1)
        {
            // 同一 hash 唯一、offset 不同：以 TOC 为准修正偏移。
            return (byHash!, byHashChecksumOffset);
        }

        if (hashCount == 0)
        {
            throw new InvalidDataException($"条目 hash={requested.Hash:X16} 不在 pak 条目表中");
        }

        throw new InvalidDataException(
            $"条目 hash={requested.Hash:X16} 在 pak 中对应 {hashCount} 个条目，且 offset={requested.Offset} 不匹配任何一个");
    }
}
