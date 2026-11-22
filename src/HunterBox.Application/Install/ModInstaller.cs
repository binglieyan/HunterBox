using HunterBox.Application.Common;
using HunterBox.Application.Mods;
using HunterBox.Domain.Archives;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;

namespace HunterBox.Application.Install;

/// <summary>一次安装的结果：记录与提示。</summary>
/// <param name="Record">安装完成的记录（已写入 <see cref="IModStore"/> 并落盘）。</param>
/// <param name="Warnings">需要提示用户的信息。</param>
public sealed record InstallResult(ModRecord Record, IReadOnlyList<string> Warnings);

/// <summary>
/// 安装用例入口：解压 → 识别 → 暂存（<see cref="Stage"/>/<see cref="StageReinstall"/>），
/// 再由 <see cref="InstallStaging.Commit"/> 原子落盘到 <c>Mods/&lt;id&gt;/files</c> 并写记录。
/// </summary>
/// <remarks>
/// 本类只做编排，解压与存储分别由 <see cref="IArchiveExtractor"/>、
/// <see cref="IModStore"/> 注入，便于测试与替换实现。暂存/提交细节见 <see cref="InstallStaging"/>：
/// 安装与重装共用同一套暂存、选择、原子提交与回滚逻辑，不再保留独立的一步式安装/重装入口。
/// </remarks>
public sealed class ModInstaller
{
    private readonly BoxPaths _paths;
    private readonly IModStore _store;
    private readonly IArchiveExtractor _extractor;
    private readonly IFileSystem _fileSystem;
    private readonly string _gameId;

    /// <summary>装配安装用例。</summary>
    /// <param name="paths">HunterBox磁盘布局。</param>
    /// <param name="store">MOD 记录存储。</param>
    /// <param name="extractor">解压实现。</param>
    /// <param name="fileSystem">文件系统端口（清理失败路径用）。</param>
    /// <param name="gameId">当前游戏稳定 ID；安装产出的记录会写入该归属，重复安装检测也只在本游戏内生效。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException"><paramref name="gameId"/> 为空。</exception>
    public ModInstaller(
        BoxPaths paths,
        IModStore store,
        IArchiveExtractor extractor,
        IFileSystem fileSystem,
        string gameId)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        _paths = paths;
        _store = store;
        _extractor = extractor;
        _fileSystem = fileSystem;
        _gameId = gameId.Trim();
    }

    /// <summary>当前游戏稳定 ID。</summary>
    public string GameId => _gameId;

    /// <summary>把来源解压到临时目录并识别，供界面预览/勾选后再提交。</summary>
    /// <param name="sourcePath">压缩包、单个文件或目录的绝对路径。</param>
    /// <param name="progress">解压进度回调（可空）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>暂存结果；调用方提交或释放。</returns>
    /// <exception cref="FileNotFoundException">来源不存在。</exception>
    /// <exception cref="InvalidDataException">包无法识别。</exception>
    public InstallStaging Stage(
        string sourcePath,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        InstallStaging.Create(_paths, _store, this, sourcePath, _gameId, _fileSystem, progress, cancellationToken);

    /// <summary>准备重装/更新：解压新包并识别，提交时替换已有记录（保留 Id/Index/GameId）。</summary>
    /// <param name="id">要重装/更新的 MOD 持久 ID。</param>
    /// <param name="sourcePath">新的压缩包、单个文件或目录路径。</param>
    /// <param name="progress">解压进度回调（可空）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>暂存结果（<see cref="InstallStaging.IsReinstall"/> 为 <see langword="true"/>）。</returns>
    /// <exception cref="InvalidOperationException">MOD 不存在、不属于当前游戏或仍处于启用状态。</exception>
    /// <exception cref="InvalidDataException">新包无法识别。</exception>
    public InstallStaging StageReinstall(
        int id,
        string sourcePath,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ModRecord record = _store.Find(id)
            ?? throw new InvalidOperationException($"MOD 不存在: {id}");
        GameScope.EnsureBelongsTo(record, _gameId);
        if (record.Enabled)
        {
            throw new InvalidOperationException("请先禁用 MOD 再重装");
        }

        return InstallStaging.Create(
            _paths, _store, this, sourcePath, _gameId, _fileSystem, progress, cancellationToken,
            reinstallTarget: record);
    }

    /// <summary>把来源解压/复制到临时目录并完成识别，供安装与重装复用。</summary>
    /// <param name="sourcePath">压缩包、单个文件或目录路径。</param>
    /// <param name="progress">解压进度回调（可空）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>临时目录、内容根、文件清单与识别结果。</returns>
    internal (string WorkDir, string ContentRoot, IReadOnlyList<string> Files, PackageInspection Inspection) StageCore(
        string sourcePath,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        bool isDirectory = _fileSystem.DirectoryExists(sourcePath);
        if (!isDirectory && !_fileSystem.FileExists(sourcePath))
        {
            throw new FileNotFoundException("MOD 来源不存在", sourcePath);
        }

        string workDir = Path.Combine(_paths.TempDirectory, Guid.NewGuid().ToString("N"));
        _fileSystem.CreateDirectory(workDir);
        try
        {
            string contentRoot = isDirectory
                ? CopyDirectoryInto(sourcePath, workDir)
                : InstallSingleSource(sourcePath, workDir, progress, cancellationToken);

            IReadOnlyList<string> files = ListFiles(contentRoot);
            PackageInspection inspection = PackageInspector.Inspect(files, _gameId);
            if (inspection.Kind == ModKind.Unknown)
            {
                throw new InvalidDataException(
                    "无法识别的 MOD 格式：请确认压缩包里有 natives/reframework/nativePC/weapon 等游戏目录，"
                    + "或包含 modinfo.ini / FOMOD 配置 / .pak 文件。");
            }

            return (workDir, contentRoot, files, inspection);
        }
        catch
        {
            _fileSystem.TryDeleteDirectory(workDir);
            throw;
        }
    }

    /// <summary>压缩包解压；不是压缩包的单文件（裸 .pak/.lua/.dll 等）直接复制进内容根。</summary>
    private string InstallSingleSource(
        string sourcePath,
        string workDir,
        IProgress<ArchiveProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (ArchiveFileProbe.IsSupportedArchive(sourcePath, _fileSystem))
        {
            return ExtractInto(sourcePath, workDir, progress, cancellationToken);
        }

        string target = Path.Combine(workDir, Path.GetFileName(sourcePath));
        _fileSystem.CopyFile(sourcePath, target, overwrite: true);
        return workDir;
    }

    /// <summary>卸载：墓碑改名 → 移除记录并保存 → 删除墓碑目录。</summary>
    /// <param name="id">MOD 持久 ID。</param>
    /// <returns>确实卸载时返回 <see langword="true"/>；记录不存在时返回 <see langword="false"/>。</returns>
    /// <exception cref="InvalidOperationException">MOD 仍处于启用状态，或不属于当前游戏。</exception>
    /// <exception cref="IOException">目录改名/保存失败按原异常类型抛出；目录恢复失败时包装为 <see cref="IOException"/>。</exception>
    /// <remarks>
    /// 先改名而不是先递归删除：索引保存失败时目录还能原位恢复，不会出现「记录还在、目录已经没了」。
    /// 只有 <see cref="IModStore.Remove"/> 与 <see cref="IModStore.Save"/> 都成功后才删除墓碑目录。
    /// </remarks>
    public bool Uninstall(int id)
    {
        ModRecord? record = _store.Find(id);
        if (record is null)
        {
            return false;
        }

        GameScope.EnsureBelongsTo(record, _gameId);

        if (record.Enabled)
        {
            throw new InvalidOperationException("请先禁用 MOD 再卸载");
        }

        string modDirectory = _paths.ModDirectory(id);
        RecoverStaleTombstone(modDirectory);
        string tombstone = modDirectory + ".old-" + Guid.NewGuid().ToString("N");
        bool moved = false;
        if (_fileSystem.DirectoryExists(modDirectory))
        {
            _fileSystem.MoveDirectory(modDirectory, tombstone);
            moved = true;
        }

        bool removed = _store.Remove(id);
        if (!removed)
        {
            if (!TryRestoreDirectory(tombstone, modDirectory, moved, out string? failure))
            {
                throw new IOException(
                    $"卸载未移除记录，且 MOD 目录未能从墓碑恢复：{tombstone}（{failure}）");
            }

            return false;
        }

        try
        {
            _store.Save();
        }
        catch (Exception ex)
        {
            // 落盘失败：记录加回内存，目录从墓碑改名回原位，保持「记录与目录都还在、可重试」。
            try
            {
                _store.Add(record);
            }
            catch (InvalidOperationException)
            {
                // 加回失败说明存储实现异常；仍以原始 Save 异常为准。
            }

            if (!TryRestoreDirectory(tombstone, modDirectory, moved, out string? restoreFailure))
            {
                throw new IOException(
                    $"卸载失败，且 MOD 目录未能从墓碑恢复；请手工把 “{tombstone}” 改回 “{modDirectory}”。"
                    + (restoreFailure is null ? string.Empty : $"\n恢复失败原因：{restoreFailure}")
                    + $"\n原始错误：{ex.Message}",
                    ex);
            }

            throw;
        }

        // 索引已经落盘成功，卸载结果不可回退；墓碑删除失败只留下可手工清理的 .old-* 目录，不能把成功报成失败。
        if (moved)
        {
            _fileSystem.TryDeleteDirectory(tombstone);
        }

        return true;
    }

    /// <summary>把墓碑目录改名回原位；返回是否已恢复（未移动视为成功）。</summary>
    private bool TryRestoreDirectory(
        string tombstone,
        string original,
        bool moved,
        out string? failure)
    {
        failure = null;
        if (!moved)
        {
            return true;
        }

        if (_fileSystem.DirectoryExists(original))
        {
            failure = "原位已存在同名目录";
            return false;
        }

        if (!_fileSystem.DirectoryExists(tombstone))
        {
            failure = "墓碑目录不存在";
            return false;
        }

        try
        {
            _fileSystem.MoveDirectory(tombstone, original);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure = ex.Message;
            return false;
        }
    }

    /// <summary>卸载前恢复上次失败留下的 <c>.old-*</c> 墓碑；多个墓碑无法判断时给出可读错误。</summary>
    private void RecoverStaleTombstone(string modDirectory)
    {
        if (_fileSystem.DirectoryExists(modDirectory))
        {
            return;
        }

        string? parent = Path.GetDirectoryName(modDirectory);
        if (string.IsNullOrEmpty(parent) || !_fileSystem.DirectoryExists(parent))
        {
            return;
        }

        string pattern = Path.GetFileName(modDirectory) + ".old-*";
        string[] tombstones = _fileSystem.EnumerateDirectories(parent, pattern);
        if (tombstones.Length == 0)
        {
            return;
        }

        if (tombstones.Length > 1)
        {
            throw new IOException(
                $"发现多个卸载残留目录，无法判断哪个属于该 MOD，请手工整理后重试：{string.Join("、", tombstones)}");
        }

        if (!TryRestoreDirectory(tombstones[0], modDirectory, moved: true, out string? failure))
        {
            throw new IOException(
                $"发现上次卸载残留的墓碑目录，但无法恢复到原位：{tombstones[0]}（{failure}）");
        }
    }

    private string ExtractInto(
        string archivePath,
        string workDir,
        IProgress<ArchiveProgress>? progress,
        CancellationToken cancellationToken)
    {
        _extractor.Extract(archivePath, workDir, new ArchiveExtractionOptions
        {
            Progress = progress,
            CancellationToken = cancellationToken,
        });
        return UnwrapSingleTopDirectory(workDir);
    }

    private string CopyDirectoryInto(string sourceDirectory, string workDir)
    {
        // 先物化清单：若目标目录被配置到源目录内部，惰性枚举会边复制边发现新文件，撑爆磁盘。
        // 安全枚举：跳过符号链接/junction，安装结果不包含链接目标内容。
        string[] files = SafeFileTree.EnumerateFiles(_fileSystem, sourceDirectory);
        foreach (string file in files)
        {
            string relative = Path.GetRelativePath(sourceDirectory, file);
            string target = Path.Combine(workDir, relative);
            _fileSystem.CreateDirectory(Path.GetDirectoryName(target)!);
            _fileSystem.CopyFile(file, target, overwrite: true);
        }

        return UnwrapSingleTopDirectory(workDir);
    }

    /// <summary>
    /// 剥掉压缩包外面多余的一层同目录；<c>natives</c> / <c>reframework</c> / <c>nativePC</c>
    /// 及各作结构目录（pl/wp/plugins/weapon/player/art/gamedesign…）本身就是内容根，不能剥。
    /// </summary>
    private string UnwrapSingleTopDirectory(string root)
    {
        string[] directories = _fileSystem.EnumerateDirectories(root);
        string[] files = _fileSystem.EnumerateFiles(root);
        if (directories.Length != 1 || files.Length != 0)
        {
            return root;
        }

        string name = Path.GetFileName(directories[0]);
        // IsKnownContentDirectoryName 已包含 natives/reframework 等全部内容根（D-24）。
        return PackageInspector.IsKnownContentDirectoryName(name)
            ? root
            : directories[0];
    }

    private IReadOnlyList<string> ListFiles(string root) =>
        [.. _fileSystem.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => PackageInspector.Normalize(Path.GetRelativePath(root, f)))
            .Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>读取包内 FOMOD 配置（标准 <c>fomod/</c> 优先，兼容根级 <c>ModuleConfig.xml</c>）。</summary>
    /// <param name="contentRoot">包内容根。</param>
    /// <param name="files">包内文件清单。</param>
    /// <returns>FOMOD 配置；不是 FOMOD 包时为 <see langword="null"/>。</returns>
    internal FomodConfig? ReadFomod(string contentRoot, IReadOnlyList<string> files)
    {
        foreach (string candidate in PackageInspector.FomodConfigCandidatePaths)
        {
            string? actual = files.FirstOrDefault(f => f.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (actual is null)
            {
                continue;
            }

            string path = Path.Combine(contentRoot, actual.Replace('/', Path.DirectorySeparatorChar));
            if (!_fileSystem.FileExists(path))
            {
                continue;
            }

            try
            {
                FomodConfig? config = FomodParser.Parse(_fileSystem.ReadAllText(path), actual, files);
                if (config is not null)
                {
                    return config;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // 读不到或解析不了配置时按普通包处理（不阻塞安装）。
            }
        }

        return null;
    }

    internal ModMetadata ReadSuggestedMetadata(
        string contentRoot,
        IReadOnlyList<string> files,
        string sourcePath,
        FomodConfig? fomod = null)
    {
        ModMetadata metadata = new();
        bool hasModInfoName = false;
        string? modInfo = files.FirstOrDefault(f =>
            f.Equals(PackageInspector.ModInfoFileName, StringComparison.OrdinalIgnoreCase)
            || f.EndsWith("/" + PackageInspector.ModInfoFileName, StringComparison.OrdinalIgnoreCase));

        if (modInfo is not null)
        {
            string path = Path.Combine(contentRoot, modInfo.Replace('/', Path.DirectorySeparatorChar));
            if (_fileSystem.FileExists(path))
            {
                metadata = ModInfoParser.Parse(_fileSystem.ReadAllText(path));
                hasModInfoName = !string.IsNullOrWhiteSpace(metadata.Name);
            }
        }

        if (fomod is not null)
        {
            // FOMOD 元数据作为默认值：名称优先 modinfo，作者/版本/描述优先 FOMOD。
            metadata = metadata with
            {
                Name = hasModInfoName ? metadata.Name : FirstNonEmpty(fomod.ModuleName, FallbackName(sourcePath)),
                Version = FirstNonEmpty(fomod.ModuleVersion, metadata.Version),
                Author = FirstNonEmpty(fomod.ModuleAuthor, metadata.Author),
                Description = FirstNonEmpty(fomod.ModuleDescription, metadata.Description),
                HomeUrl = metadata.HomeUrl ?? ExtractUrl(fomod.ModuleDescription),
            };
        }

        if (string.IsNullOrWhiteSpace(metadata.Name))
        {
            metadata = metadata with { Name = FallbackName(sourcePath) };
        }

        metadata = ApplyCaimoguLink(metadata);
        metadata = metadata with { HomeUrl = metadata.HomeUrl ?? ExtractUrl(metadata.Description) };
        return metadata;
    }

    /// <summary>踩蘑菇包名约定 <c>cmg_&lt;帖子ID&gt;_&lt;标题&gt;</c>：拆出标题并补来源链接。</summary>
    private static ModMetadata ApplyCaimoguLink(ModMetadata metadata)
    {
        string? name = metadata.Name;
        if (string.IsNullOrWhiteSpace(name)
            || !name.StartsWith("cmg_", StringComparison.OrdinalIgnoreCase))
        {
            return metadata;
        }

        int idEnd = name.IndexOf('_', 4);
        if (idEnd <= 4 || idEnd >= name.Length - 1)
        {
            return metadata;
        }

        string id = name[4..idEnd];
        string title = name[(idEnd + 1)..];
        if (id.Length == 0 || title.Length == 0 || !id.All(char.IsAsciiDigit))
        {
            return metadata;
        }

        return metadata with
        {
            Name = title,
            HomeUrl = metadata.HomeUrl ?? "https://www.caimogu.cc/post/" + id + ".html",
        };
    }

    /// <summary>从描述里取最后一个 http(s) 链接（推断 HomeUrl）。</summary>
    internal static string? ExtractUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        int index = text.LastIndexOf("http", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        string url = text[index..].Trim();
        int end = url.IndexOfAny([' ', '\r', '\n', '\t', '，', '。', ')', '）']);
        return end > 0 ? url[..end] : url;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string FallbackName(string sourcePath) =>
        Path.GetFileNameWithoutExtension(sourcePath.TrimEnd(Path.DirectorySeparatorChar, '/'));

}
