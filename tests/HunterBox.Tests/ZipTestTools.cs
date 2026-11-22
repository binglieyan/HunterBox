using System.IO.Compression;
using System.Text;

namespace HunterBox.Tests;

/// <summary>构造 ZIP 样本的工具（二进制条目 / GBK 文件名 / ZipCrypto 加密包）。</summary>
internal static class ZipTestTools
{
    /// <summary>用字节内容创建 ZIP（用于嵌套压缩包样本）。</summary>
    public static string CreateFromBytes(
        string directory,
        string fileName,
        params (string Path, byte[] Content)[] entries)
    {
        string zipPath = Path.Combine(directory, fileName);
        using FileStream file = File.Create(zipPath);
        using ZipArchive zip = new(file, ZipArchiveMode.Create);
        foreach ((string path, byte[] content) in entries)
        {
            ZipArchiveEntry entry = zip.CreateEntry(path);
            using Stream stream = entry.Open();
            stream.Write(content);
        }

        return zipPath;
    }

    /// <summary>
    /// 把 ZIP 里某个条目名替换成 GBK 编码（必须与旧名 UTF-8 字节数相同），
    /// 并清掉 UTF-8 标志位，模拟中文 Windows 生成的压缩包。
    /// </summary>
    public static string RewriteEntryNameAsGbk(string zipPath, string oldName, string newName)
    {
        byte[] oldBytes = Encoding.UTF8.GetBytes(oldName);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        byte[] newBytes = Encoding.GetEncoding(936).GetBytes(newName);
        if (oldBytes.Length != newBytes.Length)
        {
            throw new ArgumentException($"GBK 名长度 {newBytes.Length} 与旧名 {oldBytes.Length} 不一致");
        }

        byte[] data = File.ReadAllBytes(zipPath);
        ReplaceAll(data, oldBytes, newBytes);
        ClearUtf8Flag(data);
        File.WriteAllBytes(zipPath, data);
        return zipPath;
    }

    /// <summary>
    /// 创建一个传统 ZipCrypto 加密的 ZIP（单文件、Stored 压缩），供「密码猜测」测试使用。
    /// </summary>
    public static string CreateEncrypted(
        string directory,
        string fileName,
        string entryName,
        byte[] content,
        string password)
    {
        string zipPath = Path.Combine(directory, fileName);
        byte[] nameBytes = Encoding.UTF8.GetBytes(entryName);
        uint crc = Crc32.Compute(content);

        byte[] header = new byte[12];
        Random random = new(12345);
        random.NextBytes(header);
        // 传统 ZipCrypto的 12 字节加密头最后一位校验 = CRC 高字节（SharpCompress 用它验证密码）。
        header[11] = (byte)(crc >> 24);

        uint[] keys = ZipCryptoKeys(password);
        byte[] encrypted = new byte[header.Length + content.Length];
        int index = 0;
        foreach (byte value in header)
        {
            encrypted[index++] = (byte)(value ^ MountedCryptoByte(keys));
            UpdateKeys(keys, value);
        }

        foreach (byte value in content)
        {
            encrypted[index++] = (byte)(value ^ MountedCryptoByte(keys));
            UpdateKeys(keys, value);
        }

        using FileStream file = File.Create(zipPath);
        using BinaryWriter writer = new(file);
        const ushort flags = 0x0001; // 传统加密
        const ushort method = 0; // stored
        const ushort time = 0;
        const ushort date = 0x21;
        uint compressedSize = (uint)encrypted.Length;
        uint uncompressedSize = (uint)content.Length;

        long localOffset = writer.BaseStream.Position;
        writer.Write(0x04034B50u);
        writer.Write((ushort)20);
        writer.Write(flags);
        writer.Write(method);
        writer.Write(time);
        writer.Write(date);
        writer.Write(crc);
        writer.Write(compressedSize);
        writer.Write(uncompressedSize);
        writer.Write((ushort)nameBytes.Length);
        writer.Write((ushort)0);
        writer.Write(nameBytes);
        writer.Write(encrypted);

        long centralOffset = writer.BaseStream.Position;
        writer.Write(0x02014B50u);
        writer.Write((ushort)20);
        writer.Write((ushort)20);
        writer.Write(flags);
        writer.Write(method);
        writer.Write(time);
        writer.Write(date);
        writer.Write(crc);
        writer.Write(compressedSize);
        writer.Write(uncompressedSize);
        writer.Write((ushort)nameBytes.Length);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(0u);
        writer.Write((uint)localOffset);
        writer.Write(nameBytes);

        long centralEnd = writer.BaseStream.Position;
        writer.Write(0x06054B50u);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write((uint)(centralEnd - centralOffset));
        writer.Write((uint)centralOffset);
        writer.Write((ushort)0);

        return zipPath;
    }

    private static void ReplaceAll(byte[] haystack, byte[] needle, byte[] replacement)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                for (int j = 0; j < replacement.Length; j++)
                {
                    haystack[i + j] = replacement[j];
                }
            }
        }
    }

    private static void ClearUtf8Flag(byte[] data)
    {
        // 本地文件头：flags 在偏移 6；中央目录项：flags 在偏移 8。UTF-8 标志位 0x0800 = 高字节 0x08。
        for (int i = 0; i <= data.Length - 4; i++)
        {
            if (data[i] == 0x50 && data[i + 1] == 0x4B && data[i + 2] == 0x03 && data[i + 3] == 0x04)
            {
                data[i + 7] &= 0xF7;
            }
            else if (data[i] == 0x50 && data[i + 1] == 0x4B && data[i + 2] == 0x01 && data[i + 3] == 0x02)
            {
                data[i + 9] &= 0xF7;
            }
        }
    }

    private static uint[] ZipCryptoKeys(string password)
    {
        uint[] keys = [0x12345678, 0x23456789, 0x34567890];
        foreach (byte value in Encoding.UTF8.GetBytes(password))
        {
            UpdateKeys(keys, value);
        }

        return keys;
    }

    private static void UpdateKeys(uint[] keys, byte value)
    {
        keys[0] = Crc32.Update(keys[0], value);
        keys[1] = (keys[1] + (keys[0] & 0xFF)) * 134775813 + 1;
        keys[2] = Crc32.Update(keys[2], (byte)(keys[1] >> 24));
    }

    private static byte MountedCryptoByte(uint[] keys)
    {
        uint temp = keys[2] | 2;
        return (byte)((temp * (temp ^ 1)) >> 8);
    }
}

/// <summary>最小 CRC32（IEEE）实现，供测试构造 ZIP / ZipCrypto。</summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in data)
        {
            crc = Update(crc, value);
        }

        return ~crc;
    }

    public static uint Update(uint crc, byte value) =>
        AfterUpdate(crc, value);

    private static uint AfterUpdate(uint crc, byte value) =>
        Table[(crc ^ value) & 0xFF] ^ (crc >> 8);

    private static uint[] BuildTable()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320 ^ (value >> 1) : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }
}
