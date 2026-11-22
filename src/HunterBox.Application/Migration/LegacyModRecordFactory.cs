using HunterBox.Domain.Mods;

namespace HunterBox.Application.Migration;

/// <summary>旧版 <c>info.xml</c> 的元数据快照（与旧格式解析解耦，便于 Application 层测试，A-03）。</summary>
/// <param name="Name">显示名。</param>
/// <param name="ModuleName">模块名（显示名为空时的回退）。</param>
/// <param name="Category">分类。</param>
/// <param name="Version">版本。</param>
/// <param name="Author">作者。</param>
/// <param name="NexusId">NexusMods 数字 ID；0 表示没有。</param>
/// <param name="HomeUrl">来源页。</param>
/// <param name="Description">备注。</param>
public sealed record LegacyModDescriptor(
    string? Name,
    string? ModuleName,
    string? Category,
    string? Version,
    string? Author,
    int NexusId,
    string? HomeUrl,
    string? Description);

/// <summary>
/// 旧数据导入的记录构建规则（A-03）：只做纯决策，旧格式读取与文件复制仍由 Infrastructure 适配器完成。
/// </summary>
public static class LegacyModRecordFactory
{
    /// <summary>按旧 MOD 元数据与文件清单构建待导入记录（导入后统一未启用）。</summary>
    /// <param name="id">新分配的 MOD ID。</param>
    /// <param name="gameId">新HunterBox稳定游戏 ID。</param>
    /// <param name="descriptor">旧 MOD 元数据。</param>
    /// <param name="inspection">包识别结论（种类与目标目录）。</param>
    /// <param name="relativeFiles">旧 MOD 的文件相对路径清单。</param>
    /// <param name="installedAt">导入时间（由用例显式传入，A-13）。</param>
    /// <returns>尚未加入存储的 MOD 记录。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="descriptor"/>、<paramref name="inspection"/> 或 <paramref name="relativeFiles"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException"><paramref name="gameId"/> 为空。</exception>
    public static ModRecord Create(
        int id,
        string gameId,
        LegacyModDescriptor descriptor,
        PackageInspection inspection,
        IReadOnlyList<string> relativeFiles,
        DateTimeOffset installedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(relativeFiles);

        return new ModRecord
        {
            Id = id,
            GameId = gameId,
            Kind = inspection.Kind,
            TargetDirectory = inspection.TargetDirectory,
            Metadata = new ModMetadata
            {
                Name = FirstNonEmpty(descriptor.Name, descriptor.ModuleName, $"MOD {id}"),
                Category = descriptor.Category,
                Version = descriptor.Version,
                Author = descriptor.Author,
                NexusId = descriptor.NexusId > 0 ? descriptor.NexusId : null,
                HomeUrl = descriptor.HomeUrl,
                Description = descriptor.Description,
            },
            RelativeFiles = [.. relativeFiles.Select(PackageInspector.Normalize)],
            // 旧启用状态不沿用：新HunterBox以磁盘文件是否落盘为准，导入后统一未启用。
            Enabled = false,
            InstalledAt = installedAt,
        };
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
}
