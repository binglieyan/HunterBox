using HunterBox.Domain.Mods;

namespace HunterBox.Tests;

/// <summary>
/// A-08：共享的内存 <see cref="IModStore"/> 替身（原 ActivationSafetyTests / InstallAtomicityTests
/// 各有一份 FlakyStore）。可注入 Save 失败，用于验证启用/安装/重装流程在落盘失败时回滚文件与内存状态。
/// 通过锁保护内部列表，读写语义与真实仓库一致（重复 Id 抛异常）。
/// </summary>
internal sealed class FakeModStore : IModStore
{
    private readonly object _gate = new();
    private readonly List<ModRecord> _mods = [];

    /// <summary>ID 高水位：与真实仓库一致，删除后不复用。</summary>
    private int _nextId = 1;

    /// <summary>设置后 Save 抛出「磁盘只读/空间不足」的 <see cref="IOException"/>。</summary>
    public bool FailSave { get; set; }

    /// <summary>设置后 Save 抛出该异常（用于验证非 IO 异常也会回滚）。</summary>
    public Exception? SaveFailure { get; set; }

    /// <summary>Save 抛出前调用（用于模拟落盘失败时的外部目录变化）。</summary>
    public Action? OnSave { get; set; }

    public IReadOnlyList<ModRecord> Mods
    {
        get
        {
            lock (_gate)
            {
                return [.. _mods];
            }
        }
    }

    public int NextId()
    {
        lock (_gate)
        {
            // 与真实仓库一致，只返回高水位；由 Add 推进，重复调用不会消耗 ID。
            return _nextId;
        }
    }

    public ModRecord? Find(int id)
    {
        lock (_gate)
        {
            return _mods.FirstOrDefault(m => m.Id == id);
        }
    }

    public void Add(ModRecord record)
    {
        lock (_gate)
        {
            if (_mods.Exists(m => m.Id == record.Id))
            {
                throw new InvalidOperationException($"MOD ID 重复: {record.Id}");
            }

            if (record.Index <= 0)
            {
                // 与真实仓库一致：已归属记录按游戏内序号从 1 开始；
                // 未归属记录使用全局序号（各游戏共享），避免多游戏/迁移测试掩盖序号冲突。
                record.Index = string.IsNullOrWhiteSpace(record.GameId)
                    ? (_mods.Count == 0 ? 1 : _mods.Max(m => m.Index) + 1)
                    : _mods
                        .Where(m => string.Equals(m.GameId, record.GameId, StringComparison.OrdinalIgnoreCase))
                        .Select(m => m.Index)
                        .DefaultIfEmpty(0)
                        .Max() + 1;
            }

            _nextId = Math.Max(_nextId, record.Id + 1);
            _mods.Add(record);
        }
    }

    public bool Remove(int id)
    {
        lock (_gate)
        {
            ModRecord? record = _mods.FirstOrDefault(m => m.Id == id);
            return record is not null && _mods.Remove(record);
        }
    }

    public ModRecord AssignGame(int id, string gameId)
    {
        lock (_gate)
        {
            int position = _mods.FindIndex(m => m.Id == id);
            if (position < 0)
            {
                throw new InvalidOperationException($"MOD 不存在: {id}");
            }

            ModRecord existing = _mods[position];
            if (!string.IsNullOrWhiteSpace(existing.GameId))
            {
                throw new InvalidOperationException($"MOD 已归属游戏 {existing.GameId}: {id}");
            }

            int index = _mods
                .Where(m => m.BelongsTo(gameId))
                .Select(m => m.Index)
                .DefaultIfEmpty(0)
                .Max() + 1;
            ModRecord assigned = existing.WithGame(gameId, index);
            _mods[position] = assigned;
            return assigned;
        }
    }

    public void ReorderForGame(string gameId, IEnumerable<int> idsInOrder)
    {
        lock (_gate)
        {
            int index = 1;
            foreach (int id in idsInOrder)
            {
                ModRecord? record = _mods.FirstOrDefault(m => m.Id == id);
                if (record is not null && record.BelongsTo(gameId))
                {
                    record.Index = index++;
                }
            }
        }
    }

    public void Save()
    {
        OnSave?.Invoke();
        if (FailSave)
        {
            throw new IOException("模拟索引写入失败（磁盘只读/空间不足）");
        }

        if (SaveFailure is not null)
        {
            throw SaveFailure;
        }
    }
}
