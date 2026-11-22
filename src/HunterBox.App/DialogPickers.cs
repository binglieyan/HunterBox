using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;

namespace HunterBox.App;

/// <summary>把 WinUI 文件/目录选择器接到当前窗口句柄上。</summary>
internal static class DialogPickers
{
    /// <summary>选择 MOD 文件：压缩包、裸 <c>.pak</c> 或任意单文件（「全部件」过滤器）。</summary>
    /// <param name="window">用于取得窗口句柄的窗口。</param>
    /// <returns>选中的文件路径；用户取消时为空集合。</returns>
    public static async Task<IReadOnlyList<string>> PickModArchivesAsync(Window window)
    {
        FileOpenPicker picker = new();
        Initialize(picker, window);
        AddModFilters(picker);

        IReadOnlyList<Windows.Storage.StorageFile> files = await picker.PickMultipleFilesAsync();
        return [.. files.Select(f => f.Path)];
    }

    /// <summary>选择单个安装包（用于重装/更新；支持 .pak 等单文件）。</summary>
    /// <param name="window">用于取得窗口句柄的窗口。</param>
    /// <returns>文件路径；用户取消时为 <see langword="null"/>。</returns>
    public static async Task<string?> PickSingleArchiveAsync(Window window)
    {
        FileOpenPicker picker = new();
        Initialize(picker, window);
        AddModFilters(picker);

        Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    /// <summary>选择游戏根目录。</summary>
    /// <param name="window">用于取得窗口句柄的窗口。</param>
    /// <returns>目录路径；用户取消时为 <see langword="null"/>。</returns>
    public static Task<string?> PickFolderAsync(Window window) =>
        PickFolderAsync(WindowHandleOf(window));

    /// <summary>选择游戏根目录（已拿到窗口句柄时使用，供 ContentDialog 等宿主调用）。</summary>
    /// <param name="windowHandle">窗口句柄；为 0 时由系统按前台窗口处理。</param>
    /// <returns>目录路径；用户取消时为 <see langword="null"/>。</returns>
    public static async Task<string?> PickFolderAsync(nint windowHandle)
    {
        FolderPicker picker = new();
        Initialize(picker, windowHandle);
        picker.FileTypeFilter.Add("*");

        Windows.Storage.StorageFolder? folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    /// <summary>取窗口句柄（无窗口时返回 0）。</summary>
    /// <param name="window">窗口。</param>
    /// <returns>Win32 窗口句柄。</returns>
    public static nint WindowHandleOf(Window? window)
    {
        if (window is null)
        {
            return 0;
        }

        try
        {
            return WinRT.Interop.WindowNative.GetWindowHandle(window);
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    private static void AddModFilters(FileOpenPicker picker)
    {
        picker.FileTypeFilter.Add(".zip");
        picker.FileTypeFilter.Add(".7z");
        picker.FileTypeFilter.Add(".rar");
        // 裸 .pak / REF 脚本/插件单文件安装，最后放「全部文件」。
        picker.FileTypeFilter.Add(".pak");
        picker.FileTypeFilter.Add(".lua");
        picker.FileTypeFilter.Add(".dll");
        picker.FileTypeFilter.Add("*");

        // 首次打开定位下载目录（HunterBox Download 归档也在这里）。
        picker.SuggestedStartLocation = PickerLocationId.Downloads;
    }

    private static void Initialize(object picker, Window window) => Initialize(picker, WindowHandleOf(window));

    private static void Initialize(object picker, nint windowHandle)
    {
        if (windowHandle == 0)
        {
            return;
        }

        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
    }
}
