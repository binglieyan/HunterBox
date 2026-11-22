using HunterBox.Domain.Paks;

namespace HunterBox.Application.Paks;

/// <summary>
/// 整包解包 / 重打包能力：把 pak 完整解包到目录，并按记录下来的 pak 属性重新打包。
/// </summary>
/// <remarks>
/// <para>
/// 用于无法「只改条目表」的 pak：整张 TOC 加密（feature 8/24）时不能原地改写条目哈希，
/// 只能先解包（解密 TOC、解压/解密条目），在目录里改名后再整体打包。
/// </para>
/// <para>
/// 解包会做 TOC 解密（feature 8/24）、条目资源解密与 DEFLATE / ZSTD 解压；
/// 打包按 <see cref="PakRepackProfile"/> 生成包头与条目表，必要时重新加密 TOC，
/// 先写临时文件、成功后原子替换目标 pak，失败不会留下半个 pak。
/// </para>
/// </remarks>
public interface IPakRepacker
{
    /// <summary>解析 pak 包头与条目表；TOC 加密时先解密再返回。</summary>
    /// <param name="pakPath">pak 文件路径。</param>
    /// <returns>解密后的包头与条目信息。</returns>
    /// <exception cref="FileNotFoundException">pak 不存在。</exception>
    /// <exception cref="InvalidDataException">pak 结构不合法（魔数、版本、条目越界等）。</exception>
    /// <exception cref="NotSupportedException">feature 或压缩/加密方式不受支持。</exception>
    PakArchiveInfo Inspect(string pakPath);

    /// <summary>记录原 pak 的属性，供重打包时保持版本、feature、压缩/加密方式与 TOC 加密材料。</summary>
    /// <param name="pakPath">pak 文件路径。</param>
    /// <returns>重打包配置。</returns>
    /// <exception cref="FileNotFoundException">pak 不存在。</exception>
    /// <exception cref="InvalidDataException">pak 结构不合法。</exception>
    /// <exception cref="NotSupportedException">feature 或压缩/加密方式不受支持。</exception>
    PakRepackProfile Capture(string pakPath);

    /// <summary>把 pak 的全部条目解包到目录。</summary>
    /// <param name="pakPath">pak 文件路径。</param>
    /// <param name="destinationDirectory">
    /// 解包目标目录；已存在时**整体替换**（目录中不属于该 pak 的既有文件会被移除），不是合并覆盖。
    /// 调用方需要保留既有内容时应先解包到独立临时目录。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="FileNotFoundException">pak 不存在。</exception>
    /// <exception cref="InvalidDataException">pak 结构不合法、条目越界或包内路径不安全。</exception>
    /// <exception cref="NotSupportedException">TOC / 条目加密方式或压缩方式不受支持。</exception>
    /// <exception cref="OperationCanceledException">操作已取消。</exception>
    void Unpack(string pakPath, string destinationDirectory, CancellationToken cancellationToken);

    /// <summary>把 pak 的全部条目解包到目录，并指定路径清单与进度回调。</summary>
    /// <param name="pakPath">pak 文件路径。</param>
    /// <param name="destinationDirectory">
    /// 解包目标目录；已存在时**整体替换**（目录中不属于该 pak 的既有文件会被移除）。
    /// </param>
    /// <param name="nameIndex">哈希 → 路径清单；为 <see langword="null"/> 时使用实现自带的默认清单（没有则全部按未知条目处理）。</param>
    /// <param name="progress">进度回调；可为 <see langword="null"/>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="FileNotFoundException">pak 不存在。</exception>
    /// <exception cref="InvalidDataException">pak 结构不合法、条目越界或包内路径不安全。</exception>
    /// <exception cref="NotSupportedException">TOC / 条目加密方式或压缩方式不受支持。</exception>
    /// <exception cref="OperationCanceledException">操作已取消。</exception>
    void Unpack(
        string pakPath,
        string destinationDirectory,
        PakNameIndex? nameIndex,
        IProgress<PakRepackProgress>? progress,
        CancellationToken cancellationToken);

    /// <summary>把目录重新打包成 pak。</summary>
    /// <param name="sourceDirectory">源目录；目录内的相对路径就是 pak 内路径（分隔符统一为 <c>/</c>）。</param>
    /// <param name="pakPath">输出 pak 路径；已存在时在成功后原子替换。</param>
    /// <param name="profile">重打包配置，通常来自 <see cref="Capture"/>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="DirectoryNotFoundException">源目录不存在。</exception>
    /// <exception cref="ArgumentException">配置非法（版本/feature/压缩/加密方式组合不支持）。</exception>
    /// <exception cref="InvalidDataException">源目录里有无法确定条目哈希的文件，或多个文件映射到同一条目。</exception>
    /// <exception cref="OperationCanceledException">操作已取消。</exception>
    void Pack(string sourceDirectory, string pakPath, PakRepackProfile profile, CancellationToken cancellationToken);

    /// <summary>把目录重新打包成 pak，并报告进度。</summary>
    /// <param name="sourceDirectory">源目录。</param>
    /// <param name="pakPath">输出 pak 路径。</param>
    /// <param name="profile">重打包配置。</param>
    /// <param name="progress">进度回调；可为 <see langword="null"/>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="DirectoryNotFoundException">源目录不存在。</exception>
    /// <exception cref="ArgumentException">配置非法（版本/feature/压缩/加密方式组合不支持）。</exception>
    /// <exception cref="InvalidDataException">源目录里有无法确定条目哈希的文件，或多个文件映射到同一条目。</exception>
    /// <exception cref="OperationCanceledException">操作已取消。</exception>
    void Pack(
        string sourceDirectory,
        string pakPath,
        PakRepackProfile profile,
        IProgress<PakRepackProgress>? progress,
        CancellationToken cancellationToken);
}
