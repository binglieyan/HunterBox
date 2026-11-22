using System.Runtime.Versioning;
using HunterBox.Application.Saves;
using HunterBox.Domain.Layout;

namespace HunterBox.Tests;

/// <summary>
/// <see cref="SaveBackupService.Restore"/> 的故障路径（复审 N5）：
/// <list type="bullet">
/// <item>阶段 4（覆盖后哈希校验）失败时，回滚成功 → 明确“已回滚”文案；回滚也失败 → 明确“请手工检查存档目录”文案。</item>
/// <item>存档父目录不可写（Windows ACL 拒绝创建子目录）→ 在改动当前存档前抛出带上下文的 <see cref="IOException"/>，而不是裸 <see cref="UnauthorizedAccessException"/>。</item>
/// </list>
/// </summary>
[Collection("SaveBackupSizeCache")]
public class SaveBackupRestoreFaultTests
{
    private const string GameId = "mhws";

    /// <summary>把测试回调适配成故障注入钩子。</summary>
    /// <param name="action">覆盖写盘成功后执行的动作。</param>
    private sealed class ActionFaultHook(Action action) : IBackupFaultHook
    {
        /// <inheritdoc />
        public void AfterCommit() => action();
    }

    /// <summary>准备：当前存档内容为 <c>current</c>，备份内容为 <c>before</c>。</summary>
    /// <param name="afterCommitFactory">可选故障注入：根据存档目录构造「覆盖后」回调。</param>
    private static (TempDir Temp, SaveBackupService Service, string SaveDir, SaveBackup Backup) Arrange(
        Func<string, Action>? afterCommitFactory = null)
    {
        TempDir temp = TempDir.Create();
        string saveDir = temp.Dir("userdata", "save");
        File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "before");
        SaveBackupService service = new(
            new BoxPaths(temp.Path),
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            clock: null,
            manifestStore: new HunterBox.Infrastructure.Saves.JsonBackupManifestStore(),
            faultHook: afterCommitFactory is null ? null : new ActionFaultHook(afterCommitFactory(saveDir)));
        SaveBackup backup = service.Backup(GameId, saveDir);
        File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "current");
        return (temp, service, saveDir, backup);
    }

    [Fact]
    public void Restore_PostCopyVerificationFails_RollsBackAndSaysRolledBack()
    {
        (TempDir temp, SaveBackupService service, string saveDir, SaveBackup backup) = Arrange(
            dir => () => File.WriteAllText(Path.Combine(dir, "slot1.sav"), "corrupted-after-commit"));
        using (temp)
        {
            string target = Path.Combine(saveDir, "slot1.sav");
            // 阶段 3 覆盖成功后再把目标文件写坏，稳定复现“覆盖后哈希校验失败”。

            SaveBackupVerificationException ex = Assert.Throws<SaveBackupVerificationException>(
                () => service.Restore(backup, saveDir));

            Assert.Contains(
                ex.Verification.Issues,
                issue => issue.Kind == SaveBackupIssueKind.PostCopyMismatch && issue.RelativePath == "slot1.sav");
            Assert.True(ex.RolledBack);
            Assert.Equal("current", File.ReadAllText(target));
            AssertNoRestoreTempDirectories(Path.GetDirectoryName(saveDir)!);
        }
    }

    [Fact]
    public void Restore_PostCopyVerificationFailsAndRollbackFails_ThrowsIOExceptionWithManualCheckHint()
    {
        (TempDir temp, SaveBackupService service, string saveDir, SaveBackup backup) = Arrange(
            dir => () =>
            {
                string file = Path.Combine(dir, "slot1.sav");
                File.WriteAllText(file, "corrupted-after-commit");
                File.SetAttributes(file, FileAttributes.ReadOnly);
            });
        using (temp)
        {
            string target = Path.Combine(saveDir, "slot1.sav");
            // 覆盖后写坏并设只读：阶段 4 校验失败，且回滚复制覆盖只读文件会失败。

            try
            {
                // 必须是 IOException 而非 InvalidDataException：回滚失败时文案要引导用户手工检查。
                IOException ex = Assert.Throws<IOException>(() => service.Restore(backup, saveDir));

                Assert.Contains("回滚当前存档也失败", ex.Message);
                Assert.Contains("请手工检查存档目录", ex.Message);
                AggregateException aggregate = Assert.IsType<AggregateException>(ex.InnerException);
                Assert.Contains(aggregate.InnerExceptions, error => error is IOException or UnauthorizedAccessException);

                // C-01：回滚失败时快照是还原前存档的唯一完整副本，必须保留并在文案中给出路径。
                string parent = Path.GetDirectoryName(saveDir)!;
                string snapshot = Assert.Single(Directory.GetDirectories(parent, ".restore-undo-*"));
                Assert.Contains(snapshot, ex.Message);
                Assert.Equal("current", File.ReadAllText(Path.Combine(snapshot, "slot1.sav")));
                Assert.DoesNotContain(
                    Directory.GetDirectories(parent),
                    path => Path.GetFileName(path).StartsWith(".restore-staged-", StringComparison.Ordinal));
            }
            finally
            {
                File.SetAttributes(target, FileAttributes.Normal);
            }
        }
    }

    [Fact]
    public void Restore_UnexpectedFailureAfterCommit_KeepsSnapshot()
    {
        (TempDir temp, SaveBackupService service, string saveDir, SaveBackup backup) = Arrange(
            _ => () => throw new InvalidOperationException("boom"));
        using (temp)
        {
            Assert.Throws<InvalidOperationException>(() => service.Restore(backup, saveDir));

            // 覆盖已经开始后，任何非 IO 异常都不能让 finally 删掉唯一快照。
            string parent = Path.GetDirectoryName(saveDir)!;
            string snapshot = Assert.Single(Directory.GetDirectories(parent, ".restore-undo-*"));
            Assert.Equal("current", File.ReadAllText(Path.Combine(snapshot, "slot1.sav")));
        }
    }

    [AclFact(requireWriteDeny: true)]
    [SupportedOSPlatform("windows")]
    public void Restore_ReadOnlySaveParent_ThrowsReadableIOExceptionBeforeTouchingSave()
    {
        (TempDir temp, SaveBackupService service, string saveDir, SaveBackup backup) = Arrange();
        using (temp)
        {
            string parent = Path.GetDirectoryName(saveDir)!;
            using DirectoryWriteDeny deny = DirectoryWriteDeny.Apply(parent);

            // 父目录拒绝创建子目录：快照/影子目录建不出来，必须在改动当前存档前失败并给出可读原因。
            IOException ex = Assert.Throws<IOException>(() => service.Restore(backup, saveDir));

            Assert.Contains("还原影子副本", ex.Message);
            Assert.Contains("父目录只读", ex.Message);
            Assert.Contains("当前存档未被改动", ex.Message);
            Assert.Contains("2×", ex.Message);
            Assert.True(
                ex.InnerException is UnauthorizedAccessException or IOException,
                "原始系统异常必须保留在 InnerException：" + ex.InnerException?.GetType().FullName);
            Assert.Equal("current", File.ReadAllText(Path.Combine(saveDir, "slot1.sav")));
            AssertNoRestoreTempDirectories(parent);
        }
    }

    private static void AssertNoRestoreTempDirectories(string parent)
    {
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(parent),
            path => Path.GetFileName(path).StartsWith(".restore-", StringComparison.Ordinal));
    }
}
