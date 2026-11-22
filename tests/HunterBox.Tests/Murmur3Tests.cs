using System.Text;
using HunterBox.Infrastructure.Paks;

namespace HunterBox.Tests;

public class Murmur3Tests
{
    [Fact]
    public void Hash32_EmptyInputWithZeroSeed_IsZero()
    {
        Assert.Equal(0u, Murmur3.Hash32(ReadOnlySpan<byte>.Empty));
    }

    [Theory]
    [InlineData("hello", 0u, 0x248BFA47u)]
    [InlineData("a", 0u, 0x3C2569B2u)]
    public void Hash32_MatchesKnownVectors(string text, uint seed, uint expected)
    {
        Assert.Equal(expected, Murmur3.Hash32(Encoding.ASCII.GetBytes(text), seed));
    }

    [Fact]
    public void Hash32_IsDeterministic()
    {
        byte[] data = Encoding.Unicode.GetBytes(@"natives/stm/art/model/character/ch03/x.mod3");
        Assert.Equal(Murmur3.Hash32(data, 0xFFFFFFFFu), Murmur3.Hash32(data, 0xFFFFFFFFu));
    }

    /// <summary>P-13：char span 版本必须与「先转 UTF-16LE 字节再哈希」逐字节等价（省掉临时 byte[]）。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("ab")]
    [InlineData("abc")]
    [InlineData("hello")]
    [InlineData("natives/stm/art/model/character/ch03/012/body.mod3")]
    [InlineData("Natives\\STM\\Art\\Model\\Character\\ch03\\012\\Body.MOD3")]
    [InlineData("natives/stm/素材/模型/武器.mod3")]
    [InlineData("😀/emoji/路径.bin")]
    public void Hash32_CharSpan_MatchesUtf16ByteHashing(string text)
    {
        byte[] utf16LittleEndian = Encoding.Unicode.GetBytes(text);

        Assert.Equal(
            Murmur3.Hash32(utf16LittleEndian, 0xFFFFFFFFu),
            Murmur3.Hash32(text.AsSpan(), 0xFFFFFFFFu));
        Assert.Equal(
            Murmur3.Hash32(utf16LittleEndian, 0u),
            Murmur3.Hash32(text.AsSpan(), 0u));
    }
}
