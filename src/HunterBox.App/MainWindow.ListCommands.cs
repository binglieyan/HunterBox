using HunterBox.Domain.Mods;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using System.Diagnostics;

namespace HunterBox.App;

/// <summary>MainWindow 的列表命令：排序/快速移动与右键菜单。</summary>
public sealed partial class MainWindow
{

    private void MoveUp_Click(object sender, RoutedEventArgs e) =>
        RunUiAction("调整顺序失败", () => MoveAsync(-1));

    private void MoveDown_Click(object sender, RoutedEventArgs e) =>
        RunUiAction("调整顺序失败", () => MoveAsync(1));

    /// <summary>上移/下移一位（只作用于第一个选中项）。</summary>
    private async Task MoveAsync(int delta)
    {
        if (!await CanReorderAsync())
        {
            return;
        }

        ModRow row = SelectedRow!;
        List<int> order = [.. _services.ModList.GetOrder(_services.GameSession.Game.Id)];
        int from = order.IndexOf(row.Mod.Id);
        if (from < 0)
        {
            return;
        }

        await MoveToPositionAsync(row, Math.Clamp(from + delta, 0, order.Count - 1));
    }

    /// <summary>「快速移动」按钮：对话框输入目标位置（1..N，默认当前位）。</summary>
    private void MoveTo_Click(object sender, RoutedEventArgs e) =>
        RunUiAction("快速移动失败", MoveToCoreAsync);

    /// <summary>快速移动：先过排序守卫，再弹位置对话框，最后移动。</summary>
    private async Task MoveToCoreAsync()
    {
        if (!await CanReorderAsync())
        {
            return;
        }

        ModRow row = SelectedRow!;
        IReadOnlyList<int> order = _services.ModList.GetOrder(_services.GameSession.Game.Id);
        MoveToDialog dialog = new(order.Count, row.Mod.Index) { XamlRoot = Root.XamlRoot };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        int target = dialog.Position;
        if (target < 1 || target > order.Count)
        {
            await ShowMessageAsync("快速移动", $"请输入 1-{order.Count} 之间的位置");
            return;
        }

        await MoveToPositionAsync(row, target - 1);
    }

    /// <summary>右键「快速移动到顶部/中间/底部」。</summary>
    private async Task QuickMoveAsync(ModRow row, int target)
    {
        if (!await CanReorderAsync(row))
        {
            return;
        }

        IReadOnlyList<int> order = _services.ModList.GetOrder(_services.GameSession.Game.Id);
        await MoveToPositionAsync(row, Math.Clamp(target, 0, order.Count - 1));
    }

    /// <summary>排序守卫：无选中 / 搜索模式 / MOD 已启用时一律拒绝。</summary>
    private async Task<bool> CanReorderAsync(ModRow? target = null)
    {
        target ??= SelectedRow;
        if (target is null)
        {
            await ShowMessageAsync("调整顺序", "请先选中一个 MOD");
            return false;
        }
        if (IsFilterMode)
        {
            await ShowMessageAsync("调整顺序", "请先退出搜索模式：清空关键词，然后再点击搜索");
            return false;
        }
        if (target.Mod.Enabled)
        {
            await ShowMessageAsync("调整顺序", "请禁用MOD后再调整顺序");
            return false;
        }

        return true;
    }

    /// <summary>把指定 MOD 移动到目标下标（0 起始），重排序号并保持选中。</summary>
    /// <remarks>
    /// 序号以磁盘上的 index.json 为唯一真相：先改内存再落盘，落盘失败必须把内存顺序回滚回原样，
    /// 否则下一次任意 Save 会把「用户没看到的顺序」悄悄写进磁盘（激活覆盖顺序也会随之错乱）。
    /// </remarks>
    private async Task MoveToPositionAsync(ModRow row, int target)
    {
        // 顺序计算与重排由 Application 协调器负责（A-06）；窗口只处理提示、落盘与失败回滚。
        IReadOnlyList<int>? original = _services.ModList.Move(_services.GameSession.Game.Id, row.Mod.Id, target);
        if (original is null)
        {
            ViewModel.Status = $"「{row.Mod.DisplayName}」已经在第 {target + 1} 位";
            return;
        }

        try
        {
            // 大索引 fsync 不阻塞 UI 线程；失败时回到 UI 线程回滚内存顺序。
            await SaveStoreAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.ModList.Restore(_services.GameSession.Game.Id, original);
            _services.Logger.Error("保存顺序失败，已回滚内存顺序", ex);
            ViewModel.Status = "调整顺序失败（无法写入索引）：" + ex.Message;
            return;
        }

        ViewModel.AddSelected(row.Mod.Id);
        try
        {
            ReloadMods(updateStatus: false);
        }
        catch (Exception ex)
        {
            _services.Logger.Error("调整顺序后刷新列表失败", ex);
        }

        ViewModel.Status = $"已把「{row.Mod.DisplayName}」移动到第 {target + 1} 位";
        UpdateActionButtons();
    }

    private void ModsList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_operations.IsBusy)
        {
            return;
        }

        // 只有点在 MOD 行上才弹菜单：列表空白处右键不弹（默认行为）。
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not ModRow row)
        {
            return;
        }

        // 右键只作用于被点击的那一行：多选状态下也收拢为单行，避免菜单操作对象不一致。
        ModsList.SelectedItems.Clear();
        ModsList.SelectedItems.Add(row);
        ViewModel.SetSelectedIds([row.Mod.Id]);

        int count = _services.Store.ModsFor(_services.GameSession.Game.Id).Count;
        MenuFlyout menu = new();

        MenuFlyoutItem info = new() { Text = "MOD信息设置" };
        info.Click += (_, _) => RunUiAction("MOD信息设置失败", () => EditInfoAsync(row));
        menu.Items.Add(info);
        MenuFlyoutItem rewrite = new() { Text = "MOD文件修改(取消启用MOD才能修改)" };
        rewrite.Click += (_, _) => RunUiAction("MOD文件修改失败", () => RewriteModelsAsync(row));
        menu.Items.Add(rewrite);

        menu.Items.Add(new MenuFlyoutSeparator());

        string nexusGamePath = string.IsNullOrWhiteSpace(_services.GameSession.Game.NexusGamePath) ? "monsterhunterwilds" : _services.GameSession.Game.NexusGamePath;
        string? nexusUrl = ModLinks.NexusUrl(row.Mod.Metadata.NexusId, nexusGamePath);
        MenuFlyoutItem nexus = new() { Text = "跳转到NexusMods查看", IsEnabled = nexusUrl is not null };
        nexus.Click += (_, _) => OpenUrl(nexusUrl);
        menu.Items.Add(nexus);
        MenuFlyoutItem home = new()
        {
            Text = "打开MOD来源网址",
            IsEnabled = ModLinks.IsHttpUrl(row.Mod.Metadata.HomeUrl),
        };
        home.Click += (_, _) => OpenUrl(row.Mod.Metadata.HomeUrl);
        menu.Items.Add(home);
        MenuFlyoutItem folder = new() { Text = "查看MOD的文件" };
        folder.Click += (_, _) => OpenFolder(row.Mod);
        menu.Items.Add(folder);

        menu.Items.Add(new MenuFlyoutSeparator());

        MenuFlyoutItem top = new() { Text = "快速移动到顶部" };
        top.Click += (_, _) => RunUiAction("快速移动失败", () => QuickMoveAsync(row, 0));
        menu.Items.Add(top);
        MenuFlyoutItem middle = new() { Text = "快速移动到中间" };
        middle.Click += (_, _) => RunUiAction("快速移动失败", () => QuickMoveAsync(row, count / 2));
        menu.Items.Add(middle);
        MenuFlyoutItem bottom = new() { Text = "快速移动到底部" };
        bottom.Click += (_, _) => RunUiAction("快速移动失败", () => QuickMoveAsync(row, count - 1));
        menu.Items.Add(bottom);

        menu.Items.Add(new MenuFlyoutSeparator());

        MenuFlyoutItem toggle = new() { Text = row.Mod.Enabled ? "禁用" : "启用" };
        toggle.Click += (_, _) => RunUiAction("切换启用状态失败", () => ToggleAsync(row.Mod));
        menu.Items.Add(toggle);
        MenuFlyoutItem reinstall = new() { Text = "重装" };
        reinstall.Click += (_, _) => RunUiAction("重装失败", ReinstallCoreAsync);
        menu.Items.Add(reinstall);
        MenuFlyoutItem recopy = new() { Text = "重新复制" };
        recopy.Click += (_, _) => RunUiAction("重新复制失败", () => RecopyOneAsync(row));
        menu.Items.Add(recopy);
        MenuFlyoutItem uninstall = new() { Text = "卸载" };
        uninstall.Click += (_, _) => RunUiAction("卸载失败", UninstallCoreAsync);
        menu.Items.Add(uninstall);

        menu.ShowAt(ModsList, new FlyoutShowOptions { Position = e.GetPosition(ModsList) });
        e.Handled = true;
    }

    private void OpenUrl(string? url)
    {
        if (!ModLinks.IsHttpUrl(url))
        {
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(url!) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ViewModel.Status = "打开链接失败：" + ex.Message;
        }
    }

    private void OpenFolder(ModRecord mod)
    {
        string directory = _services.Paths.FilesDirectory(mod.Id);
        if (!_services.FileSystem.DirectoryExists(directory))
        {
            ViewModel.Status = "MOD 目录不存在";
            return;
        }
        Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }

}
