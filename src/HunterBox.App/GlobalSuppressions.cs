using System.Diagnostics.CodeAnalysis;

// WindowsAppSDK 自带的 UndockedRegFreeWinRT-AutoInitializer.cs 无法修改，
// 这里只对该生成文件的 P/Invoke 定向抑制 CA5392；
// 手写 P/Invoke 必须带 DefaultDllImportSearchPaths(System32)，由 LayerSourceGuardTests 门禁保证。
[assembly: SuppressMessage(
    "Security",
    "CA5392:Use DefaultDllImportSearchPaths attribute for P/Invokes",
    Scope = "member",
    Target = "~M:Microsoft.Windows.Foundation.UndockedRegFreeWinRTCS.NativeMethods.WindowsAppRuntime_EnsureIsLoaded",
    Justification = "WindowsAppSDK 生成文件中的 P/Invoke，无法添加搜索路径属性；手写 P/Invoke 已全部标注 System32。")]
