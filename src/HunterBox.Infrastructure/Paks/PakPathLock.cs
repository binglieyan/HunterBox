namespace HunterBox.Infrastructure.Paks;

/// <summary>
/// 同一 pak 路径的进程内互斥锁：改名（读改写 TOC）与前像恢复必须串行，避免恢复与写入交错。
/// </summary>
/// <remarks>
/// 按路径引用计数：最后一个使用者释放时移除锁对象，长期运行不会累积。
/// <see cref="Monitor"/> 可重入，同一线程在改名作用域内再次调用恢复不会自锁。
/// 读锁与写锁共用同一把排他锁：当前读取频率低、操作基本串行，未做读写分离。
/// 仅进程内有效；多个实例/外部工具同时写同一 pak 不在支持范围。
/// </remarks>
internal static class PakPathLock
{
    private static readonly Dictionary<string, Entry> Locks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>获取指定路径的互斥锁。</summary>
    /// <param name="path">规范化后的 pak 绝对路径。</param>
    /// <returns>释放时自动退出监视器并减少引用计数。</returns>
    public static Scope Acquire(string path)
    {
        Entry entry;
        lock (Locks)
        {
            if (!Locks.TryGetValue(path, out entry!))
            {
                entry = new Entry();
                Locks[path] = entry;
            }

            entry.Users++;
        }

        try
        {
            Monitor.Enter(entry.Sync);
        }
        catch
        {
            // Monitor.Enter 被中断（ThreadInterruptedException 等）时回退引用计数，避免锁对象永不回收。
            lock (Locks)
            {
                entry.Users--;
                if (entry.Users == 0)
                {
                    Locks.Remove(path);
                }
            }

            throw;
        }

        return new Scope(path, entry);
    }

    internal sealed class Entry
    {
        public object Sync { get; } = new();

        public int Users { get; set; }
    }

    /// <summary>一次路径锁持有；Dispose 时退出监视器并在无人使用时移除锁对象。</summary>
    internal readonly struct Scope : IDisposable
    {
        private readonly string _path;
        private readonly Entry _entry;

        public Scope(string path, Entry entry)
        {
            _path = path;
            _entry = entry;
        }

        public void Dispose()
        {
            Monitor.Exit(_entry.Sync);
            lock (Locks)
            {
                _entry.Users--;
                if (_entry.Users == 0)
                {
                    Locks.Remove(_path);
                }
            }
        }
    }
}
