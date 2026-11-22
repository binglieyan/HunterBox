using HunterBox.Application.Common;
using HunterBox.Infrastructure.IO;

namespace HunterBox.Tests;

/// <summary>
/// A-10：撤销作用域工厂把「登记补偿动作」与具体实现解耦；
/// 默认工厂必须创建可用的文件级回滚日志，并支持回滚成功回调与参数校验。
/// </summary>
public class FileUndoScopeFactoryTests
{
    [Fact]
    public void Create_ReturnsScopeThatRollsBackCreatedFile()
    {
        using TempDir temp = TempDir.Create();
        string destination = Path.Combine(temp.Path, "game", "new.txt");
        FileUndoScopeFactory factory = new(SystemFileSystem.Instance);
        using IUndoScope scope = factory.Create(Path.Combine(temp.Path, "undo"));

        scope.RecordCreated(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, "brand-new");

        List<string> warnings = [];
        scope.Rollback(warnings);

        Assert.Empty(warnings);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void Create_WithCallback_NotifiesRollbackAppliedPath()
    {
        using TempDir temp = TempDir.Create();
        string destination = Path.Combine(temp.Path, "game", "new.txt");
        List<string> applied = [];
        FileUndoScopeFactory factory = new(SystemFileSystem.Instance);
        using IUndoScope scope = factory.Create(Path.Combine(temp.Path, "undo"), applied.Add);

        scope.RecordCreated(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, "brand-new");

        scope.Rollback([]);

        Assert.Equal(new[] { destination }, applied);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_EmptyUndoRoot_Throws(string undoRoot)
    {
        FileUndoScopeFactory factory = new(SystemFileSystem.Instance);

        Assert.Throws<ArgumentException>(() => factory.Create(undoRoot));
    }
}
