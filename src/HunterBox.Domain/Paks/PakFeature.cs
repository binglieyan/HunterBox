namespace HunterBox.Domain.Paks;

/// <summary>pak feature 位的纯判定：避免各适配器各写一遍位运算。</summary>
public static class PakFeature
{
    /// <summary>TOC（条目表）是否已加密（feature 含 0x8 位，即 feature 8 / 24）。</summary>
    /// <param name="feature">包头 feature 位。</param>
    /// <returns>加密时返回 <see langword="true"/>。</returns>
    public static bool IsTocEncrypted(short feature) => (feature & 8) != 0;
}
