namespace HunterBox.Tests;

/// <summary>需要创建超过 260 字符路径的用例：环境不支持时在发现阶段跳过。</summary>
internal sealed class LongPathFactAttribute : FactAttribute
{
    public LongPathFactAttribute()
    {
        if (!LongPathSupport.IsSupported)
        {
            Skip = "当前环境不支持创建超过 260 字符的路径，跳过长路径用例。";
        }
    }
}

/// <summary>长路径支持探测：真实创建一次超长目录链，失败时由 <see cref="LongPathFactAttribute"/> 跳过。</summary>
internal static class LongPathSupport
{
    private static readonly Lazy<bool> Support = new(Probe);

    /// <summary>当前环境是否支持长路径（仅 Windows 且能创建 <c>\\?\</c> 超长目录）。</summary>
    public static bool IsSupported => Support.Value;

    private static bool Probe()
    {
        string root = Path.Combine(Path.GetTempPath(), "hunterbox-longpath-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            string deep = root;
            for (int index = 0; index < 12; index++)
            {
                deep = Path.Combine(deep, "long-directory-name-" + index);
            }

            if (deep.Length <= 260)
            {
                return false;
            }

            Directory.CreateDirectory(@"\\?\" + deep);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 探测残留由系统 Temp 清理。
            }
        }
    }
}
