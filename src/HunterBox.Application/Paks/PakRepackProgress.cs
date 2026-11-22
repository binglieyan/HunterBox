namespace HunterBox.Application.Paks;

/// <summary>解包 / 重打包的进度快照。</summary>
/// <param name="CompletedEntries">已处理的条目数。</param>
/// <param name="TotalEntries">总条目数（解包时为 pak 条目数，打包时为源目录文件数）。</param>
/// <param name="CurrentPath">刚处理的条目相对路径；仅在处理完成一条后上报，其它时候为 <see langword="null"/>。</param>
public readonly record struct PakRepackProgress(
    int CompletedEntries,
    int TotalEntries,
    string? CurrentPath);
