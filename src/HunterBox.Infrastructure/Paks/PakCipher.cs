using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;

namespace HunterBox.Infrastructure.Paks;

/// <summary>
/// pak 的两层混淆：整张条目表（TOC）的密钥流混淆，以及压缩条目的资源加密。
/// </summary>
/// <remarks>
/// <para>
/// 两者都是「公钥可逆混淆」：用固定模数与公钥指数做 <see cref="BigInteger.ModPow"/>
/// 还原 128/256 字节密钥块，再参与异或或整数除法。没有私钥也能生成可被同一算法还原的数据，
/// 因此重打包时可以重新加密 TOC / 条目。
/// </para>
/// <para>
/// 常量是格式固定的公开材料（模数与指数），与具体游戏内容无关。
/// </para>
/// <para>
/// 资源加密的 128 字节密文块只承载 8 字节明文，因此每 8 字节要做一次 <see cref="BigInteger.ModPow"/>；
/// 这是游戏格式本身决定的，无法在不改变互操作性的前提下合并。大条目按块并行解密以吃满多核，
/// 吞吐上限属于既有实现的文档化取舍。
/// </para>
/// </remarks>
internal static class PakCipher
{
    /// <summary>TOC 加密密钥块固定长度。</summary>
    public const int TocKeyBlockSize = 128;

    /// <summary>TOC 密钥流混淆的模数（1024 位，小端字节序）。</summary>
    private static readonly BigInteger TocModulus = new(
        new byte[]
        {
            125, 11, 248, 193, 124, 35, 253, 59, 212, 117,
            22, 210, 51, 33, 216, 16, 113, 249, 124, 209,
            52, 147, 186, 119, 38, 252, 171, 44, 238, 218,
            217, 28, 137, 231, 41, 123, 221, 138, 174, 80,
            57, 182, 1, 109, 33, 137, 93, 165, 161, 62,
            162, 192, 140, 147, 19, 54, 101, 235, 232, 223,
            6, 23, 103, 150, 6, 43, 172, 35, 237, 140,
            183, 139, 144, 173, 234, 113, 196, 64, 68, 157,
            28, 123, 186, 196, 182, 45, 214, 210, 75, 98,
            214, 38, 252, 116, 32, 7, 236, 227, 89, 154,
            230, 175, 185, 168, 53, 139, 224, 232, 211, 205,
            69, 101, 176, 145, 196, 149, 27, 243, 35, 30,
            198, 113, 207, 62, 53, 45, 107, 227,
        },
        isUnsigned: true,
        isBigEndian: false);

    /// <summary>TOC 混淆使用的公钥指数（65537）。</summary>
    private static readonly BigInteger TocExponent = 65537;

    /// <summary>资源加密的模数（256 位，小端字节序）。</summary>
    private static readonly BigInteger ResourceModulus = new(
        new byte[]
        {
            19, 215, 156, 137, 136, 145, 72, 16, 215, 170,
            120, 174, 248, 89, 223, 125, 60, 67, 160, 208,
            187, 54, 119, 181, 240, 92, 2, 175, 101, 216,
            119, 3,
        },
        isUnsigned: true,
        isBigEndian: false);

    /// <summary>资源加密使用的公钥指数（256 位，小端字节序）。</summary>
    private static readonly BigInteger ResourceExponent = new(
        new byte[]
        {
            192, 194, 119, 31, 91, 52, 106, 1, 199, 212,
            215, 133, 46, 66, 43, 59, 22, 58, 23, 19,
            22, 234, 131, 48, 48, 223, 63, 244, 37, 147,
            32, 1,
        },
        isUnsigned: true,
        isBigEndian: false);

    /// <summary>资源加密每块承载的明文字节数。</summary>
    private const int ResourceBlockPlainSize = 8;

    /// <summary>资源加密每个密文块的长度。</summary>
    private const int ResourceBlockCipherSize = 128;

    /// <summary>加密 / 解密整张 TOC（异或密钥流，两个方向相同）。</summary>
    /// <param name="toc">条目表字节（原地不修改，返回新数组）。</param>
    /// <param name="encryptedKeyBlock">包内 128 字节密钥块。</param>
    /// <returns>处理后的 TOC 字节。</returns>
    public static byte[] CryptToc(ReadOnlySpan<byte> toc, ReadOnlySpan<byte> encryptedKeyBlock)
    {
        byte[] key = DecryptTocKey(encryptedKeyBlock);
        byte[] result = toc.ToArray();

        // 两个密钥下标用递增计数器代替逐字节取模，TOC 较大时省掉两次除法（P-19）。
        int keyIndex32 = 0;
        int keyIndex29 = 0;
        for (int i = 0; i < result.Length; i++)
        {
            result[i] ^= (byte)(i + (key[keyIndex32] * key[keyIndex29]));
            if (++keyIndex32 == 32)
            {
                keyIndex32 = 0;
            }

            if (++keyIndex29 == 29)
            {
                keyIndex29 = 0;
            }
        }

        return result;
    }

    /// <summary>生成一个可用的 128 字节 TOC 密钥块（保证解出的密钥长度足够参与混淆）。</summary>
    /// <returns>密钥块。</returns>
    public static byte[] CreateTocKeyBlock()
    {
        byte[] block = new byte[TocKeyBlockSize];
        while (true)
        {
            RandomNumberGenerator.Fill(block);
            try
            {
                _ = DecryptTocKey(block);
                return block;
            }
            catch (InvalidDataException)
            {
                // 解出的密钥过短（概率极低），重新生成。
            }
        }
    }

    /// <summary>
    /// 创建资源加密条目的流式解密流：按 128 字节块逐块解密，内存占用只与读缓冲有关。
    /// </summary>
    /// <param name="source">从条目起始位置开始的 pak 数据流。</param>
    /// <param name="storedSize">条目在 pak 里的存储长度（含 8 字节长度头）。</param>
    /// <returns>只读、不可定位的解密流（不关闭 <paramref name="source"/>）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="storedSize"/> 为负。</exception>
    public static Stream CreateResourceDecryptionStream(Stream source, long storedSize)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(storedSize);
        return new ResourceDecryptionStream(source, storedSize);
    }

    /// <summary>解密单个 128 字节块，把明文写进 <paramref name="target"/>（至少 8 字节），返回有效字节数。</summary>
    private static int DecryptResourceBlock(ReadOnlySpan<byte> blockData, Span<byte> target)
    {
        // 格式里这两个 64 字节大数是「无符号」小端整数（BN_bin2bn 语义）：
        // 最高位为 1 时按有符号解析会得到负数，ModPow 结果与商也会带符号，解出的数据错位。
        BigInteger value = new(blockData[..64], isUnsigned: true, isBigEndian: false);
        BigInteger dividend = new(blockData[64..], isUnsigned: true, isBigEndian: false);
        BigInteger divisor = BigInteger.ModPow(value, ResourceExponent, ResourceModulus);
        if (divisor.IsZero)
        {
            throw new InvalidDataException("资源加密块无效（除数为 0）");
        }

        byte[] quotient = BigInteger.Divide(dividend, divisor).ToByteArray(isUnsigned: true, isBigEndian: false);
        if (quotient.Length > ResourceBlockPlainSize)
        {
            throw new InvalidDataException("资源加密块解出的数据超过单块 8 字节上限");
        }

        target[..ResourceBlockPlainSize].Clear();
        quotient.CopyTo(target);
        return quotient.Length;
    }

    /// <summary>计算资源加密后的数据长度（8 字节长度头 + N×128 字节密文块），不实际加密。</summary>
    /// <param name="plaintextLength">待加密（压缩后）数据长度。</param>
    /// <returns>加密后的存储长度。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="plaintextLength"/> 为负。</exception>
    /// <exception cref="NotSupportedException">加密封装后超出 2GB 上限。</exception>
    public static long EncryptResourceSize(long plaintextLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(plaintextLength);
        // 每 8 字节明文膨胀为 128 字节密文（固定 16 倍）：用 long 计算避免 int 回绕。
        long blockCount = (plaintextLength + ResourceBlockPlainSize - 1) / ResourceBlockPlainSize;
        long totalLength = 8 + (blockCount * ResourceBlockCipherSize);
        if (totalLength > int.MaxValue)
        {
            throw new NotSupportedException(
                $"条目过大无法加密（明文 {plaintextLength} 字节，加密后约 {totalLength} 字节，超出 2GB 上限）");
        }

        return totalLength;
    }

    /// <summary>把一个压缩数据块加密成资源加密条目格式。</summary>
    /// <remarks>测试/诊断用参考实现；生产打包走流式 <see cref="EncryptResource(Stream, Stream, long, CancellationToken)"/>。</remarks>
    /// <param name="payload">压缩数据。</param>
    /// <returns>8 字节长度 + 128 字节块的加密数据。</returns>
    /// <exception cref="NotSupportedException">数据过大，加密后超出 2GB 上限。</exception>
    public static byte[] EncryptResource(ReadOnlySpan<byte> payload)
    {
        long blockCount = ((long)payload.Length + ResourceBlockPlainSize - 1) / ResourceBlockPlainSize;
        byte[] result = new byte[EncryptResourceSize(payload.Length)];
        BinaryPrimitives.WriteInt64LittleEndian(result, payload.Length);

        for (int block = 0; block < blockCount; block++)
        {
            int sourceOffset = block * ResourceBlockPlainSize;
            int chunkLength = Math.Min(ResourceBlockPlainSize, payload.Length - sourceOffset);
            EncryptResourceBlock(
                payload.Slice(sourceOffset, chunkLength),
                result.AsSpan(8 + (block * ResourceBlockCipherSize), ResourceBlockCipherSize));
        }

        return result;
    }

    /// <summary>
    /// 把压缩数据流式加密到目标流（8 字节长度头 + N×128 字节块），
    /// 峰值内存只与单块缓冲有关，不再一次性持有压缩数据与 16 倍密文。
    /// </summary>
    /// <param name="source">压缩数据流（从头读到尾）。</param>
    /// <param name="destination">目标流；调用方负责关闭/定位。</param>
    /// <param name="plaintextLength">待加密的压缩数据长度。</param>
    /// <param name="cancellationToken">取消令牌；每 4096 个块检查一次。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="plaintextLength"/> 为负。</exception>
    /// <exception cref="NotSupportedException">数据过大，加密后超出 2GB 上限。</exception>
    /// <exception cref="OperationCanceledException">令牌被触发。</exception>
    public static void EncryptResource(
        Stream source,
        Stream destination,
        long plaintextLength,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegative(plaintextLength);
        _ = EncryptResourceSize(plaintextLength);
        cancellationToken.ThrowIfCancellationRequested();

        Span<byte> lengthBytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(lengthBytes, plaintextLength);
        destination.Write(lengthBytes);

        Span<byte> chunk = stackalloc byte[ResourceBlockPlainSize];
        Span<byte> block = stackalloc byte[ResourceBlockCipherSize];
        long remaining = plaintextLength;
        long blockIndex = 0;
        while (remaining > 0)
        {
            if ((blockIndex & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            int chunkLength = (int)Math.Min(ResourceBlockPlainSize, remaining);
            chunk.Clear();
            source.ReadExactly(chunk[..chunkLength]);
            EncryptResourceBlock(chunk, block);
            destination.Write(block);
            remaining -= chunkLength;
            blockIndex++;
        }
    }

    /// <summary>加密单个 128 字节块（最多 8 字节明文，尾部零填充）。</summary>
    private static void EncryptResourceBlock(ReadOnlySpan<byte> plaintext, Span<byte> target)
    {
        ulong chunk = 0;
        for (int i = 0; i < plaintext.Length; i++)
        {
            chunk |= (ulong)plaintext[i] << (i * 8);
        }

        Span<byte> valueBytes = stackalloc byte[64];
        BigInteger value;
        BigInteger divisor;
        do
        {
            valueBytes.Clear();
            RandomNumberGenerator.Fill(valueBytes);
            // 无符号小端解析：不再需要清最高位来「保持正数」。
            value = new BigInteger(valueBytes, isUnsigned: true, isBigEndian: false);
            divisor = BigInteger.ModPow(value, ResourceExponent, ResourceModulus);
        }
        while (value.IsZero || divisor.IsZero);

        BigInteger dividend = divisor * chunk;
        target.Clear();
        if (!value.TryWriteBytes(target[..64], out _, isUnsigned: true, isBigEndian: false))
        {
            throw new InvalidDataException("资源加密密钥写出失败");
        }

        if (!dividend.TryWriteBytes(target[64..ResourceBlockCipherSize], out _, isUnsigned: true, isBigEndian: false))
        {
            throw new InvalidDataException("资源加密数据写出失败");
        }
    }

    /// <summary>
    /// 资源加密条目的只读流式解密：每次填充读一批密文块并逐块解密，明文按需交给解压流。
    /// </summary>
    [SuppressMessage(
        "Usage",
        "CA2213:Disposable fields should be disposed",
        Justification = "只读解密视图不拥有底层流，关闭由调用方负责。")]
    private sealed class ResourceDecryptionStream : Stream
    {
        /// <summary>单次填充处理的密文块数（64KB 明文 → 1MB 密文读缓冲）。</summary>
        private const int BlocksPerFill = (64 * 1024) / ResourceBlockPlainSize;

        /// <summary>块数达到该值时并行解密（单块是独立的 ModPow，大条目能吃到多核）。</summary>
        private const int ParallelDecryptThreshold = 512;

        private readonly Stream _source;
        private readonly long _storedSize;
        private readonly byte[] _cipher = new byte[BlocksPerFill * ResourceBlockCipherSize];
        private readonly byte[] _plain = new byte[BlocksPerFill * ResourceBlockPlainSize];
        private long _blockCount;
        private long _blocksRead;
        private long _remaining;
        private int _plainOffset;
        private int _plainCount;
        private bool _initialized;
        private bool _disposed;

        public ResourceDecryptionStream(Stream source, long storedSize)
        {
            _source = source;
            _storedSize = storedSize;
        }

        public override bool CanRead => !_disposed;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (buffer.Length - offset < count)
            {
                throw new ArgumentException("offset + count 超出缓冲区长度", nameof(count));
            }

            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Initialize();
            if (buffer.IsEmpty || _remaining <= 0)
            {
                return 0;
            }

            if (_plainOffset >= _plainCount && !Fill())
            {
                return 0;
            }

            int take = (int)Math.Min(Math.Min(buffer.Length, _plainCount - _plainOffset), _remaining);
            _plain.AsSpan(_plainOffset, take).CopyTo(buffer);
            _plainOffset += take;
            _remaining -= take;
            return take;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <summary>只释放自身状态，pak 数据流由调用方管理。</summary>
        /// <param name="disposing">是否释放托管资源。</param>
        protected override void Dispose(bool disposing)
        {
            _disposed = true;
            base.Dispose(disposing);
        }

        /// <summary>读取并校验 8 字节长度头，确定要输出的明文字节数。</summary>
        private void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            if (_storedSize < 8)
            {
                throw new InvalidDataException($"资源加密条目长度 {_storedSize} 不足 8 字节");
            }

            long cipherLength = _storedSize - 8;
            if (cipherLength % ResourceBlockCipherSize != 0)
            {
                throw new InvalidDataException(
                    $"资源加密条目数据长度 {_storedSize} 不是 128 字节块的整数倍");
            }

            _blockCount = cipherLength / ResourceBlockCipherSize;
            Span<byte> header = stackalloc byte[8];
            _source.ReadExactly(header);
            long declaredLength = BinaryPrimitives.ReadInt64LittleEndian(header);
            if (declaredLength < 0)
            {
                throw new InvalidDataException($"资源加密条目声明长度异常: {declaredLength}");
            }

            _remaining = Math.Min(declaredLength, _blockCount * ResourceBlockPlainSize);
        }

        /// <summary>读入下一批密文块并解密到明文缓冲；没有剩余块时返回 false。</summary>
        private bool Fill()
        {
            int blocks = 0;
            while (blocks < BlocksPerFill && _blocksRead < _blockCount)
            {
                _source.ReadExactly(_cipher.AsSpan(blocks * ResourceBlockCipherSize, ResourceBlockCipherSize));
                blocks++;
                _blocksRead++;
            }

            // 每个 128 字节块独立（各写自己的 8 字节明文槽），大条目按块并行解密。
            DecryptBlocks(blocks);

            _plainOffset = 0;
            _plainCount = blocks * ResourceBlockPlainSize;
            return _plainCount > 0;
        }

        /// <summary>解密本批全部块：块数够多时并行，否则顺序执行（异常类型保持 <see cref="InvalidDataException"/>）。</summary>
        /// <param name="blocks">本批块数。</param>
        private void DecryptBlocks(int blocks)
        {
            if (blocks < ParallelDecryptThreshold)
            {
                for (int block = 0; block < blocks; block++)
                {
                    DecryptBlockAt(block);
                }

                return;
            }

            try
            {
                Parallel.For(0, blocks, DecryptBlockAt);
            }
            catch (AggregateException exception)
            {
                if (exception.InnerExceptions.Count == 1)
                {
                    ExceptionDispatchInfo.Capture(exception.InnerExceptions[0]).Throw();
                }

                // 多块同时失败：统一成 InvalidDataException，避免调用方收到未约定的聚合异常。
                throw new InvalidDataException(
                    $"资源加密条目有 {exception.InnerExceptions.Count} 个数据块解密失败",
                    exception);
            }
        }

        private void DecryptBlockAt(int block) =>
            DecryptResourceBlock(
                _cipher.AsSpan(block * ResourceBlockCipherSize, ResourceBlockCipherSize),
                _plain.AsSpan(block * ResourceBlockPlainSize, ResourceBlockPlainSize));
    }

    private static byte[] DecryptTocKey(ReadOnlySpan<byte> encryptedKeyBlock)
    {
        if (encryptedKeyBlock.Length != TocKeyBlockSize)
        {
            throw new InvalidDataException(
                $"TOC 密钥块长度必须是 {TocKeyBlockSize} 字节，实际为 {encryptedKeyBlock.Length} 字节");
        }

        BigInteger value = new(encryptedKeyBlock, isUnsigned: true, isBigEndian: false);
        BigInteger key = BigInteger.ModPow(value, TocExponent, TocModulus);
        byte[] keyBytes = key.ToByteArray(isUnsigned: true, isBigEndian: false);
        if (keyBytes.Length < 32)
        {
            throw new InvalidDataException("TOC 密钥块解出的密钥过短，无法用于混淆");
        }

        return keyBytes;
    }
}
