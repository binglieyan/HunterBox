using System.Security.Cryptography;
using HunterBox.Domain.Paks;

namespace HunterBox.Infrastructure.Paks;

/// <summary>
/// 用文件内容的 MD5 作为指纹，用于判断两个 pak 是否字节相同（内容相同只放一份）。
/// </summary>
/// <remarks>
/// 只在 pak 同名或去重判断时调用，属于低频操作；大文件读一次即可。
/// MD5 仅用于「内容是否相同」的去重判断，不参与任何安全判定，因此只求速度。
/// </remarks>
public sealed class FileContentFingerprint : IContentFingerprint
{
    /// <inheritdoc />
    /// <exception cref="FileNotFoundException">文件不存在。</exception>
    public string GetFingerprint(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        using FileStream stream = File.OpenRead(fullPath);
        byte[] hash = MD5.HashData(stream);
        return Convert.ToHexString(hash);
    }
}
