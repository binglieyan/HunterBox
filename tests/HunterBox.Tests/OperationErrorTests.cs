using HunterBox.Application.Common;
using HunterBox.Application.Saves;
using HunterBox.Domain.Paks;

namespace HunterBox.Tests;

/// <summary>A-12：操作错误按类别区分业务/环境/未知故障，未知故障不把原始消息给用户。</summary>
public class OperationErrorTests
{
    [Fact]
    public void From_InvalidOperation_IsBusinessWithMessage()
    {
        OperationError error = OperationError.From(new InvalidOperationException("游戏正在运行，请先退出游戏"));

        Assert.Equal(OperationErrorKind.Business, error.Kind);
        Assert.Equal("游戏正在运行，请先退出游戏", error.UserMessage);
    }

    [Fact]
    public void From_Unexpected_DoesNotLeakTechnicalMessage()
    {
        OperationError error = OperationError.From(new InvalidCastException("0xDEADBEEF 内部结构损坏"));

        Assert.Equal(OperationErrorKind.Unexpected, error.Kind);
        Assert.DoesNotContain("0xDEADBEEF", error.UserMessage);
        Assert.Contains("0xDEADBEEF", error.TechnicalDetail);
    }

    [Fact]
    public void From_IOException_IsEnvironmentWithMessage()
    {
        OperationError error = OperationError.From(new IOException("磁盘已满"));

        Assert.Equal(OperationErrorKind.Environment, error.Kind);
        Assert.Equal("磁盘已满", error.UserMessage);
    }

    [Fact]
    public void From_Canceled_IsCanceledKind()
    {
        OperationError error = OperationError.From(new OperationCanceledException());

        Assert.Equal(OperationErrorKind.Canceled, error.Kind);
        Assert.Equal("操作已取消", error.UserMessage);
    }

    [Fact]
    public void From_SaveBackupVerification_IsBusinessWithStructuredMessage()
    {
        SaveBackupVerification verification = new(
            HasManifest: true,
            FileCount: 2,
            Issues: [new SaveBackupIssue(SaveBackupIssueKind.HashMismatch, "slot1.sav")]);
        OperationError error = OperationError.From(new SaveBackupVerificationException(verification, rolledBack: true));

        Assert.Equal(OperationErrorKind.Business, error.Kind);
        Assert.Contains("1 个文件", error.UserMessage, StringComparison.Ordinal);
        Assert.Contains("已回滚", error.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void From_ComException_IsUnexpectedWithGenericMessage()
    {
        OperationError error = OperationError.From(new System.Runtime.InteropServices.COMException("0x80004005 内部错误"));

        Assert.Equal(OperationErrorKind.Unexpected, error.Kind);
        Assert.DoesNotContain("0x80004005", error.UserMessage, StringComparison.Ordinal);
        Assert.Contains("0x80004005", error.TechnicalDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void From_AggregateOfIOException_IsEnvironmentAndKeepsAggregateMessage()
    {
        OperationError error = OperationError.From(
            new AggregateException("清理过程中索引保存失败：已禁用 3/10 个 MOD", new IOException("磁盘已满")));

        Assert.Equal(OperationErrorKind.Environment, error.Kind);
        Assert.Contains("已禁用 3/10", error.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void From_AggregateWithUnexpectedInner_IsUnexpectedWithoutLeakingMessage()
    {
        OperationError error = OperationError.From(
            new AggregateException("包装消息", new InvalidCastException("0xDEADBEEF 内部结构损坏")));

        Assert.Equal(OperationErrorKind.Unexpected, error.Kind);
        Assert.DoesNotContain("0xDEADBEEF", error.UserMessage, StringComparison.Ordinal);
        Assert.Contains("0xDEADBEEF", error.TechnicalDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void From_AggregateWithMixedKinds_IsUnexpected()
    {
        OperationError error = OperationError.From(
            new AggregateException("包装消息", new IOException("磁盘已满"), new InvalidOperationException("业务失败")));

        Assert.Equal(OperationErrorKind.Unexpected, error.Kind);
        Assert.DoesNotContain("磁盘已满", error.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void From_BusinessAggregate_KeepsProgressMessageForMixedAndEmptyInnerExceptions()
    {
        OperationBusinessAggregateException mixed = new(
            "清理过程中索引保存失败：已禁用 5/7 个 MOD",
            new IOException("磁盘已满"),
            new InvalidOperationException("业务失败"));
        OperationBusinessAggregateException empty = new("清理过程中索引保存失败：已禁用 5/7 个 MOD");

        Assert.All(
            new[] { mixed, empty },
            aggregate =>
            {
                OperationError error = OperationError.From(aggregate);
                Assert.Equal(OperationErrorKind.Business, error.Kind);
                Assert.Contains("已禁用 5/7", error.UserMessage, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void From_PakConflict_IsBusinessWithConflictMessage()
    {
        PakSource owner = new(1, "已装 MOD", "cool.pak", @"C:\mods\cool.pak");
        PakSource incoming = new(2, "待启用 MOD", "cool.pak", @"C:\mods2\cool.pak");
        PakConflict conflict = new("cool.pak", incoming, owner);
        OperationError error = OperationError.From(new PakConflictException([conflict]));

        Assert.Equal(OperationErrorKind.Business, error.Kind);
        Assert.Contains("cool.pak", error.UserMessage);
    }
}
