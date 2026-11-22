using HunterBox.Domain.Paks;
using HunterBox.Infrastructure.Paks;

namespace HunterBox.Tests;

/// <summary>
/// 资源加密的互操作回归：解其它工具 / 游戏生成的密文（密钥大数最高位为 1）。
/// 本工具自己的 <c>EncryptResource</c> 曾主动清最高位，会把「有符号解析」的问题完全掩盖，
/// 所以必须用独立构造的向量验证。
/// </summary>
public class PakCipherInteropTests
{
    [Fact]
    public void Unpack_ResourceCiphertextWithHighBitKey_Decodes()
    {
        using TempDir temp = TempDir.Create();
        const string relativePath = "natives/stm/art/model/character/ch03/000/000/secret.tex.28";
        byte[] content = PakCodecTestTools.CreateCompressibleBytes(4096);
        byte[] compressed = PakCodecTestTools.Deflate(content);
        byte[] encrypted = PakCodecTestTools.EncryptResourceWithHighBitKey(compressed);

        // 第 63 字节最高位被置 1：按有符号解析时密钥大数为负。
        Assert.True(
            (encrypted[8 + 63] & 0x80) != 0,
            "测试向量应使用最高位为 1 的密钥（否则测不出有符号解析的 bug）");

        ulong hash = PakPathHasher.HashPath(relativePath);
        PakTestEntry entry = new(hash, encrypted, content.LongLength, 1L | (1L << 16));
        BuiltPak built = PakCodecTestTools.BuildPak(temp.Path, "interop.pak", 0, entry);
        PakNameIndex index = PakPathHasher.BuildIndex([relativePath]);

        string destination = temp.Dir("out");
        new PakRepacker().Unpack(built.Path, destination, index, progress: null, CancellationToken.None);

        string extracted = Path.Combine(destination, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(content, File.ReadAllBytes(extracted));
    }

    /// <summary>0–16 字节的合法载荷（含跨块边界）都必须能完整往返。</summary>
    [Fact]
    public void DecryptResource_RoundTripsPayloadLengthsZeroToSixteen()
    {
        for (int length = 0; length <= 16; length++)
        {
            byte[] payload = PakCodecTestTools.CreateBytes(length, seed: length + 1);
            byte[] encrypted = PakCipher.EncryptResource(payload);

            Assert.Equal(payload, PakCodecTestTools.DecryptResource(encrypted));
        }
    }

    /// <summary>长度头/块长度不合法时统一抛 InvalidDataException，不能泄漏越界异常。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(8 + 127)]
    public void DecryptResource_InvalidLength_ThrowsInvalidDataException(int length)
    {
        byte[] data = new byte[length];
        Assert.Throws<InvalidDataException>(() => PakCodecTestTools.DecryptResource(data));
    }

    /// <summary>流式加密的输出长度与整块加密一致，且能被整块/流式解密还原。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(16)]
    [InlineData(32_768)]
    public void EncryptResourceStream_RoundTripsAndMatchesStoredSize(int length)
    {
        byte[] payload = PakCodecTestTools.CreateBytes(length, seed: length + 13);
        using MemoryStream source = new(payload, writable: false);
        using MemoryStream encrypted = new();
        PakCipher.EncryptResource(source, encrypted, payload.LongLength);

        byte[] bytes = encrypted.ToArray();
        Assert.Equal(PakCipher.EncryptResourceSize(payload.LongLength), bytes.LongLength);
        Assert.Equal(payload, PakCodecTestTools.DecryptResource(bytes));
    }

    /// <summary>已取消的令牌必须在流式加密开始前/块边界生效。</summary>
    [Fact]
    public void EncryptResourceStream_CanceledToken_Throws()
    {
        byte[] payload = PakCodecTestTools.CreateBytes(1024, seed: 21);
        using MemoryStream source = new(payload, writable: false);
        using MemoryStream destination = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            PakCipher.EncryptResource(source, destination, payload.LongLength, cancellation.Token));
    }

    /// <summary>流式解密必须与整块解密逐字节一致（跨多个读缓冲填充）。</summary>
    [Fact]
    public void CreateResourceDecryptionStream_MatchesBufferDecrypt_ForLargePayload()
    {
        // 32KB 已跨越多个 777 字节读缓冲与 8 字节加密块，保留流式边界覆盖并缩短墙钟。
        byte[] payload = PakCodecTestTools.CreateBytes(32_768, seed: 7);
        byte[] encrypted = PakCodecTestTools.EncryptResource(payload);
        using MemoryStream source = new(encrypted, writable: false);
        using Stream stream = PakCipher.CreateResourceDecryptionStream(source, encrypted.LongLength);

        using MemoryStream output = new();
        byte[] buffer = new byte[777];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, read);
        }

        Assert.Equal(payload, output.ToArray());
        Assert.Equal(payload, PakCodecTestTools.DecryptResource(encrypted));
    }

    /// <summary>仅加密（不压缩）的大条目解包走流式路径，内容与声明长度都正确。</summary>
    [Fact]
    public void Unpack_StoredEncryptedLargeEntry_StreamsToFile()
    {
        using TempDir temp = TempDir.Create();
        const string relativePath = "natives/stm/art/model/character/ch03/000/000/large.bin";
        byte[] content = PakCodecTestTools.CreateBytes(64 * 1024, seed: 8);
        ulong hash = PakPathHasher.HashPath(relativePath);
        PakTestEntry entry = new(
            hash,
            PakCodecTestTools.EncryptResource(content),
            content.LongLength,
            1L << 16);
        BuiltPak built = PakCodecTestTools.BuildPak(temp.Path, "large-encrypted.pak", 0, entry);
        PakNameIndex index = PakPathHasher.BuildIndex([relativePath]);

        string destination = temp.Dir("out");
        new PakRepacker().Unpack(built.Path, destination, index, progress: null, CancellationToken.None);

        string extracted = Path.Combine(destination, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(content, File.ReadAllBytes(extracted));
    }
}
