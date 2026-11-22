namespace HunterBox.Application.Common;

/// <summary>
/// 默认撤销作用域工厂：创建基于 <see cref="FileUndoJournal"/> 的文件级回滚日志。
/// </summary>
public sealed class FileUndoScopeFactory : IUndoScopeFactory
{
    private readonly IFileSystem _fileSystem;

    /// <summary>装配工厂。</summary>
    /// <param name="fileSystem">文件系统端口（撤销目录的复制、移动与清理）。</param>
    /// <exception cref="ArgumentNullException"><paramref name="fileSystem"/> 为 <see langword="null"/>。</exception>
    public FileUndoScopeFactory(IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _fileSystem = fileSystem;
    }

    /// <inheritdoc />
    public IUndoScope Create(string undoRoot, Action<string>? onRollbackApplied = null) =>
        new FileUndoJournal(undoRoot, _fileSystem, onRollbackApplied);
}
