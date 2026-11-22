namespace HunterBox.Domain.Mods;

/// <summary>MOD的种类判定结果。</summary>
public enum ModKind
{
    /// <summary>无法识别的包，安装时应拒绝。</summary>
    Unknown,

    /// <summary>带已知内容根目录（<c>natives</c>/<c>reframework</c>/<c>nativepc</c>/<c>pl</c>/<c>wp</c> 等）的散装 MOD。</summary>
    LooseFiles,

    /// <summary>只含 <c>.lua</c> 脚本；按包内路径安装，不自动创建 autorun。</summary>
    RefScripts,

    /// <summary>只含 <c>.dll</c> 插件；按包内路径安装，不自动创建插件目录。</summary>
    RefPlugins,

    /// <summary>包内含 <c>.pak</c> 文件，交给 pak 放置规则处理。</summary>
    PakFiles,

    /// <summary>历史分类：旧版本识别的 REFramework 本体；当前没有生产者，仅旧索引记录可能命中。</summary>
    RefFrameworkItself,

    /// <summary>FOMOD 包，需要先让用户选择组件。</summary>
    Fomod,
}

/// <summary>包识别结论。</summary>
/// <param name="Kind">识别出的种类。</param>
/// <param name="TargetDirectory">相对游戏根目录的落点；新安装恒为 <see langword="null"/>，表示按包内路径安装。</param>
/// <param name="HasModInfo">包内是否带 <c>modinfo.ini</c>。</param>
/// <param name="Warnings">需要展示给用户的提示信息，无提示时为空集合。</param>
public sealed record PackageInspection(
    ModKind Kind,
    string? TargetDirectory,
    bool HasModInfo,
    IReadOnlyList<string> Warnings);
