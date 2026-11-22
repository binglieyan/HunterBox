namespace HunterBox.Application.Settings;

/// <summary>
/// 设置读写端口：消费者只依赖本接口，不关心配置来自 INI 文件还是测试替身。
/// </summary>
/// <remarks>
/// <para>
/// 生产实现是 Infrastructure 层的 INI 配置；测试或宿主可换成内存假实现，
/// 在不碰磁盘的前提下打桩「只读降级」「写入失败」等场景。
/// </para>
/// <para>
/// 只读降级语义由实现负责：<see cref="IsReadOnly"/> 为 <see langword="true"/> 时，
/// <see cref="Set"/>、<see cref="SetBool"/>、<see cref="Remove"/> 与 <see cref="UpdateBatch"/>
/// 只改内存、不写磁盘且不抛异常，并通过 <see cref="LoadWarning"/> 给出可读提示。
/// </para>
/// </remarks>
public interface ISettings
{
    /// <summary>配置是否处于只读降级（加载失败时写入只改内存、不落盘）。</summary>
    bool IsReadOnly { get; }

    /// <summary>只读降级的用户可读提示；正常加载时为 <see langword="null"/>。</summary>
    string? LoadWarning { get; }

    /// <summary>读取字符串配置（实现可兼容旧键名）。</summary>
    /// <param name="section">段名。</param>
    /// <param name="key">键名。</param>
    /// <param name="fallback">缺省值。</param>
    /// <returns>配置值或 <paramref name="fallback"/>。</returns>
    string Get(string section, string key, string fallback = "");

    /// <summary>读取布尔配置（实现约定 <c>true</c>/<c>1</c> 视为真）。</summary>
    /// <param name="section">段名。</param>
    /// <param name="key">键名。</param>
    /// <param name="fallback">缺省值。</param>
    /// <returns>布尔值。</returns>
    bool GetBool(string section, string key, bool fallback = false);

    /// <summary>写入字符串配置；落盘时机由实现决定。</summary>
    /// <param name="section">段名。</param>
    /// <param name="key">键名。</param>
    /// <param name="value">值；<see langword="null"/> 视为空串。</param>
    void Set(string section, string key, string? value);

    /// <summary>写入布尔值。</summary>
    /// <param name="section">段名。</param>
    /// <param name="key">键名。</param>
    /// <param name="value">布尔值。</param>
    void SetBool(string section, string key, bool value);

    /// <summary>删除一个键（不存在时为空操作）。</summary>
    /// <param name="section">段名。</param>
    /// <param name="key">键名。</param>
    void Remove(string section, string key);

    /// <summary>批量执行多次设置写入，由实现合并落盘（通常只写一次）。</summary>
    /// <param name="update">批量修改委托；参数为同一个设置端口。</param>
    /// <exception cref="ArgumentNullException"><paramref name="update"/> 为 <see langword="null"/>。</exception>
    void UpdateBatch(Action<ISettings> update);
}
