using System.Runtime.InteropServices;

namespace HunterBox.App;

/// <summary>
/// 给 WinUI 3 窗口加最小尺寸限制（WinUI 没有托管 API，通过子类化处理 WM_GETMINMAXINFO 实现）。
/// </summary>
internal sealed class WindowMinSize
{
    private const uint WmGetMinMaxInfo = 0x0024;
    private readonly SubclassProc _callback;
    private readonly int _minWidthDip;
    private readonly int _minHeightDip;
    private bool _installed;

    /// <summary>构造最小尺寸钩子。</summary>
    /// <param name="minWidthDip">最小宽度（DIP）。</param>
    /// <param name="minHeightDip">最小高度（DIP）。</param>
    public WindowMinSize(int minWidthDip, int minHeightDip)
    {
        _minWidthDip = minWidthDip;
        _minHeightDip = minHeightDip;
        _callback = OnMessage;
    }

    /// <summary>把钩子挂到窗口句柄上；重复调用无副作用。</summary>
    /// <param name="hwnd">窗口句柄。</param>
    public void Install(nint hwnd)
    {
        if (_installed || hwnd == 0)
        {
            return;
        }

        _installed = SetWindowSubclass(hwnd, _callback, 1, 0);
    }

    private nint OnMessage(nint hwnd, uint msg, nint wParam, nint lParam, nuint idSubclass, nuint refData)
    {
        if (msg == WmGetMinMaxInfo)
        {
            MINMAXINFO info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            double scale = GetDpiForWindow(hwnd) / 96.0;
            info.MinTrackX = (int)Math.Round(_minWidthDip * Math.Max(scale, 1.0));
            info.MinTrackY = (int)Math.Round(_minHeightDip * Math.Max(scale, 1.0));
            Marshal.StructureToPtr(info, lParam, false);
            return 0;
        }

        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public int ReservedX;
        public int ReservedY;
        public int MaxSizeX;
        public int MaxSizeY;
        public int MaxPositionX;
        public int MaxPositionY;
        public int MinTrackX;
        public int MinTrackY;
        public int MaxTrackX;
        public int MaxTrackY;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProc(
        nint hwnd, uint msg, nint wParam, nint lParam, nuint idSubclass, nuint refData);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        nint hwnd, SubclassProc callback, nuint idSubclass, nuint refData);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
