using HunterBox.Application.Common;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Models;
using HunterBox.Domain.Mods;

namespace HunterBox.Application.Models;

/// <summary>散装文件模型改写结果。</summary>
/// <param name="FilesMoved">被改名的文件数（只改路径，不改文件内容）。</param>
/// <param name="ModelIds">改写后识别到的完整模型 ID（护甲/猫 <c>fid_sid</c>、武器 <c>AA_BBBB</c> 等）。</param>
public sealed record LooseModelRewriteReport(int FilesMoved, IReadOnlyList<string> ModelIds)
{
    /// <summary>因目标路径已被占用而跳过改名的冲突说明；无冲突时为空。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// 散装模型 MOD 的模型身份改写：按完整身份把源路径里的模型片段
/// （护甲 <c>fid/sid</c>、武器 <c>AA/BBBB</c>，以及各作的 prefab / 骨骼 / 文件名 token）
/// 一起改成目标身份。
/// </summary>
/// <remarks>
/// 严格：散装只做 <c>IFileSystem.MoveFile</c> 改名，**不改文件内容**（贴图路径也保持原样），
/// 且只处理属于该模型身份的路径（荒野 <c>art/model</c>、<c>_prefab</c>、骨骼；
/// 崛起 <c>player/mod</c>、<c>weapon</c>、prefab、骨骼；世界 <c>nativepc/pl|wp</c>），
/// 脚本/配置/无关二进制不会被误改。
/// </remarks>
public sealed class LooseModelRewriteService
{
    private readonly BoxPaths _paths;
    private readonly IFileSystem _fileSystem;

    /// <summary>装配服务。</summary>
    /// <param name="paths">HunterBox磁盘布局。</param>
    /// <param name="fileSystem">文件系统端口（存在性判断、改名与空目录清理）。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public LooseModelRewriteService(BoxPaths paths, IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(fileSystem);
        _paths = paths;
        _fileSystem = fileSystem;
    }

    /// <summary>按完整模型身份改写某个散装 MOD 的文件路径。</summary>
    /// <param name="mod">目标 MOD（需已安装、且文件在HunterBox目录里）。</param>
    /// <param name="from">源模型身份路径形式（如 <c>ch03/012/003</c>、<c>rise:wp/GreatSword/001</c>、<c>world:wp/bow001</c>）。</param>
    /// <param name="to">目标模型身份路径形式（须与源同游戏、同类别、同类型）。</param>
    /// <returns>改写统计。</returns>
    /// <exception cref="ArgumentException">身份非法或解析失败、源/目标游戏类别类型不一致。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="mod"/> 为 <see langword="null"/>。</exception>
    public LooseModelRewriteReport Rewrite(ModRecord mod, string from, string to)
    {
        ArgumentNullException.ThrowIfNull(mod);
        if (!ModelIdentity.TryParse(from, out ModelIdentity fromIdentity))
        {
            throw new ArgumentException(
                $"源模型身份不合法: {from}（应为 ch03/012/003、rise:pl/f/060、world:wp/bow001 等完整身份形式）");
        }

        if (!ModelIdentity.TryParse(to, out ModelIdentity toIdentity))
        {
            throw new ArgumentException(
                $"目标模型身份不合法: {to}（应为 ch03/012/003、rise:pl/f/060、world:wp/bow001 等完整身份形式）");
        }

        ModelIdRewriter.ValidateIdentityPair(fromIdentity, toIdentity);

        string filesDir = _paths.FilesDirectory(mod.Id);
        if (!_fileSystem.DirectoryExists(filesDir))
        {
            return new LooseModelRewriteReport(0, []);
        }

        int moved = 0;
        List<string> newFiles = [];
        List<string> warnings = [];
        List<(string Relative, string TargetRelative)> moves = [];

        // 先全量预检：目标已存在时绝不静默覆盖 MOD 自身另一份文件。
        // 冲突文件保留原路径并记录 warning，其余文件继续改名。
        foreach (string relative in mod.RelativeFiles)
        {
            string targetRelative = ModelIdRewriter.TryRewriteIdentityPath(
                relative,
                fromIdentity,
                toIdentity,
                out string rewritten)
                ? rewritten
                : relative;

            if (string.Equals(targetRelative, relative, StringComparison.OrdinalIgnoreCase))
            {
                newFiles.Add(relative);
                continue;
            }

            string source = Path.Combine(filesDir, relative.Replace('/', Path.DirectorySeparatorChar));
            string target = Path.Combine(filesDir, targetRelative.Replace('/', Path.DirectorySeparatorChar));
            if (!_fileSystem.FileExists(source))
            {
                // 源文件已不存在：不要凭空把记录改成不存在的目标路径。
                newFiles.Add(relative);
                warnings.Add($"源文件不存在，保留原路径：{relative}");
                continue;
            }

            if (_fileSystem.FileExists(target))
            {
                newFiles.Add(relative);
                warnings.Add($"目标路径已被占用，保留原路径不覆盖：{relative} → {targetRelative}");
                continue;
            }

            moves.Add((relative, targetRelative));
            newFiles.Add(targetRelative);
        }

        // 批内两条源路径可能改写后落到同一目标（大小写变体、重复记录、不同源目录折叠）。
        // 磁盘预检对「目标尚不存在」的批内冲突无效；这里按目标分组，整组跳过并保留原路径，
        // 避免第二个 MoveFile 抛 IOException 后整批回滚。
        Dictionary<string, List<int>> targets = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < moves.Count; i++)
        {
            if (!targets.TryGetValue(moves[i].TargetRelative, out List<int>? indexes))
            {
                indexes = [];
                targets[moves[i].TargetRelative] = indexes;
            }

            indexes.Add(i);
        }

        HashSet<int> conflicted = [];
        foreach ((string targetRelative, List<int> indexes) in targets)
        {
            if (indexes.Count < 2)
            {
                continue;
            }

            foreach (int index in indexes)
            {
                string relative = moves[index].Relative;
                _ = newFiles.Remove(targetRelative);
                newFiles.Add(relative);
                warnings.Add($"目标路径与本次另一文件冲突，保留原路径：{relative} → {targetRelative}");
                conflicted.Add(index);
            }
        }

        if (conflicted.Count > 0)
        {
            moves = [.. moves.Where((_, index) => !conflicted.Contains(index))];
        }

        List<(string Source, string Target)> movedPairs = [];
        try
        {
            foreach ((string relative, string targetRelative) in moves)
            {
                string source = Path.Combine(filesDir, relative.Replace('/', Path.DirectorySeparatorChar));
                string target = Path.Combine(filesDir, targetRelative.Replace('/', Path.DirectorySeparatorChar));
                _fileSystem.CreateDirectory(Path.GetDirectoryName(target)!);
                _fileSystem.MoveFile(source, target, overwrite: false);
                movedPairs.Add((source, target));
                moved++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 已移动的文件逆序回滚，保证目录与记录仍是改名前的状态。
            for (int i = movedPairs.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (_fileSystem.FileExists(movedPairs[i].Target)
                        && !_fileSystem.FileExists(movedPairs[i].Source))
                    {
                        _fileSystem.MoveFile(movedPairs[i].Target, movedPairs[i].Source, overwrite: false);
                    }
                }
                catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"改名回滚失败：{movedPairs[i].Target}（{rollbackError.Message}）");
                }
            }

            throw new IOException(
                "散装模型改名失败，已尝试回滚本次移动的文件；MOD 记录未修改。" + ex.Message,
                ex);
        }

        mod.RelativeFiles = newFiles;
        if (moved > 0)
        {
            DirectoryPruner.DeleteEmptyDirectories(filesDir, _fileSystem);
        }

        IReadOnlyList<ModelIdentity> identities = ModelPathRecognizer.FindIdentities(newFiles);
        return new LooseModelRewriteReport(moved, [.. identities.Select(i => i.Code)])
        {
            Warnings = warnings,
        };
    }
}
