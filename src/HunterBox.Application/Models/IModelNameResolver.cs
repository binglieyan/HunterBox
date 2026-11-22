namespace HunterBox.Application.Models;

/// <summary>
/// 模型显示名解析端口：把模型路径/身份映射成名称表里的可读名字。
/// </summary>
/// <remarks>
/// 名称表数据由 Infrastructure 的内置资源提供；Application 的模型分类/文件夹扫描只依赖本接口，
/// 不直接引用 Infrastructure 的静态名称表实现（分层与可测性）。
/// </remarks>
public interface IModelNameResolver
{
    /// <summary>按模型路径查名字。</summary>
    /// <param name="path">pak 内或散装文件的相对路径。</param>
    /// <returns>名字；识别不到或名称表没有时返回 <see langword="null"/>。</returns>
    string? Resolve(string path);
}
