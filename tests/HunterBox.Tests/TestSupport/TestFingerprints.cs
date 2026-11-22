using HunterBox.Application.Paks;
using HunterBox.Infrastructure.IO;
using HunterBox.Infrastructure.Paks;

namespace HunterBox.Tests;

/// <summary>测试用内容指纹缓存：包装真实整文件哈希与文件系统端口。</summary>
internal static class TestFingerprints
{
    /// <summary>创建进程级指纹缓存实例。</summary>
    /// <returns>新的 <see cref="PakFingerprintCache"/>。</returns>
    public static PakFingerprintCache Create() =>
        new(new FileContentFingerprint(), SystemFileSystem.Instance);
}
