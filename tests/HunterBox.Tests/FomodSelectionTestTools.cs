using HunterBox.Application.Install;
using HunterBox.Domain.Mods;

namespace HunterBox.Tests;

/// <summary>测试支撑：按插件名构造 FOMOD 选择状态并提交（生产界面直接使用 <see cref="FomodSelectionState"/>）。</summary>
internal static class FomodSelectionTestTools
{
    /// <summary>按插件名构造选择状态（只调用公开的 <see cref="FomodSelectionState.SetSelected"/>）。</summary>
    /// <remarks>
    /// 先取消未被点名的可取消项，避免 Recommended 等默认选中项残留；
    /// 单选组的取消按控件语义被忽略，点名项通过 SetSelected(true) 自然完成互斥切换。
    /// </remarks>
    /// <param name="staging">已解析 FOMOD 的安装暂存。</param>
    /// <param name="selectedPluginNames">选中的插件名。</param>
    /// <returns>选择状态。</returns>
    /// <exception cref="InvalidOperationException">该包不是 FOMOD。</exception>
    public static FomodSelectionState CreateByName(
        InstallStaging staging,
        IReadOnlyCollection<string> selectedPluginNames)
    {
        ArgumentNullException.ThrowIfNull(staging);
        ArgumentNullException.ThrowIfNull(selectedPluginNames);
        if (staging.Fomod is null)
        {
            throw new InvalidOperationException("该 MOD 不是 FOMOD 包");
        }

        FomodSelectionState selection = FomodSelectionState.Create(staging.Fomod, staging.Files);
        HashSet<string> wanted = new(
            selectedPluginNames.Where(name => !string.IsNullOrWhiteSpace(name)),
            StringComparer.OrdinalIgnoreCase);

        foreach (FomodSelectionEntry entry in selection.Entries)
        {
            if (!wanted.Contains(entry.Plugin.Name)
                && !FomodSelectionState.IsLocked(entry)
                && selection.IsSelected(entry))
            {
                selection.SetSelected(entry, false);
            }
        }

        foreach (FomodSelectionEntry entry in selection.Entries)
        {
            if (wanted.Contains(entry.Plugin.Name) && !FomodSelectionState.IsLocked(entry))
            {
                selection.SetSelected(entry, true);
            }
        }

        return selection;
    }

    /// <summary>按插件名提交 FOMOD 安装（测试支撑，直接调用 <see cref="InstallStaging.CommitFomod"/>）。</summary>
    /// <param name="staging">已解析 FOMOD 的安装暂存。</param>
    /// <param name="selectedPluginNames">选中的插件名。</param>
    /// <param name="metadata">覆盖元数据。</param>
    /// <param name="archiveMode">安装源归档方式。</param>
    /// <returns>安装结果。</returns>
    public static InstallResult CommitByName(
        InstallStaging staging,
        IReadOnlyCollection<string> selectedPluginNames,
        ModMetadata? metadata = null,
        InstallSourceMode archiveMode = InstallSourceMode.None)
    {
        FomodSelectionState selection = CreateByName(staging, selectedPluginNames);
        return staging.CommitFomod(selection, metadata, archiveMode);
    }
}
