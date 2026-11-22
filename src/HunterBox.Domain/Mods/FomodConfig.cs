using System.Xml.Linq;

namespace HunterBox.Domain.Mods;

/// <summary>FOMOD的分组选择方式（对齐 NexusMods <c>GroupTypeEnum</c>）。</summary>
public enum FomodGroupType
{
    /// <summary>必选一个（单选）。</summary>
    SelectExactlyOne,

    /// <summary>至多选一个。</summary>
    SelectAtMostOne,

    /// <summary>任选多个。</summary>
    SelectAny,

    /// <summary>全部必选。</summary>
    SelectAll,

    /// <summary>至少选择一个。</summary>
    SelectAtLeastOne,
}

/// <summary>FOMOD 插件类型（对齐 NexusMods <c>PluginTypeEnum</c>）。</summary>
public enum FomodPluginType
{
    /// <summary>必需，用户不能取消。</summary>
    Required,

    /// <summary>可选（默认不选）。</summary>
    Optional,

    /// <summary>推荐（默认选中，可取消）。</summary>
    Recommended,

    /// <summary>不可用：永不选中、不可勾选，也不参与分组校验与安装。</summary>
    NotUsable,

    /// <summary>条件可用：按可选处理（默认不选，可手动勾选）。</summary>
    CouldBeUsable,
}

/// <summary>依赖组合方式。</summary>
public enum FomodDependencyOperator
{
    /// <summary>全部满足。</summary>
    And,

    /// <summary>任一满足。</summary>
    Or,
}

/// <summary>文件依赖的期望状态（对齐 <c>InstallState</c>）。</summary>
public enum FomodFileState
{
    /// <summary>文件必须存在。</summary>
    Active,

    /// <summary>文件必须不存在。</summary>
    Inactive,

    /// <summary>文件必须不存在（与 <see cref="Inactive"/> 等价；仅兼容个别安装器写出的 <c>missing</c> 值）。</summary>
    Missing,
}

/// <summary>FOMOD 文件映射（包内源 → 安装目标）。</summary>
/// <param name="Source">包内源文件相对路径。</param>
/// <param name="Destination">安装后相对游戏根目录的路径。</param>
/// <param name="Priority">安装优先级（越小越先；对应 XML的 <c>priority</c> 属性）。</param>
public sealed record FomodFileMapping(string Source, string Destination, int Priority = 0);

/// <summary>FOMOD 条件标记。</summary>
/// <param name="Name">条件标记名。</param>
/// <param name="Value">标记值（可空）。</param>
public sealed record FomodFlag(string Name, string? Value);

/// <summary>FOMOD 可选项（插件）。</summary>
/// <param name="Name">选项名。</param>
/// <param name="Description">选项说明。</param>
/// <param name="Files">该选项带进来的文件映射。</param>
/// <param name="Type">选项类型；默认可选。</param>
/// <param name="Image">选项预览图（包内相对路径）。</param>
/// <param name="ConditionFlags">选中该选项时设置的标记。</param>
public sealed record FomodPlugin(
    string Name,
    string? Description,
    IReadOnlyList<FomodFileMapping> Files,
    FomodPluginType Type = FomodPluginType.Optional,
    string? Image = null,
    IReadOnlyList<FomodFlag>? ConditionFlags = null)
{
    private static readonly IReadOnlyList<FomodFlag> NoFlags = [];

    /// <summary>选中时设置的标记（从不为 <see langword="null"/>；未设置时复用同一空集合实例）。</summary>
    public IReadOnlyList<FomodFlag> Flags => ConditionFlags ?? NoFlags;
}

/// <summary>FOMOD 选择分组。</summary>
/// <param name="Name">分组名。</param>
/// <param name="Type">选择方式。</param>
/// <param name="Plugins">可选项。</param>
public sealed record FomodGroup(string Name, FomodGroupType Type, IReadOnlyList<FomodPlugin> Plugins);

/// <summary>FOMOD 安装步骤。</summary>
/// <param name="Name">安装步骤名。</param>
/// <param name="Visible">步骤可见性依赖；<see langword="null"/> 表示始终可见。</param>
/// <param name="Groups">该步骤下的可选分组。</param>
public sealed record FomodInstallStep(
    string Name,
    FomodDependency? Visible,
    IReadOnlyList<FomodGroup> Groups);

/// <summary>FOMOD 文件依赖：要求某个包内文件处于指定状态。</summary>
/// <param name="File">被检查的文件（包内相对路径）。</param>
/// <param name="State">期望状态。</param>
public sealed record FomodFileDependency(string File, FomodFileState State = FomodFileState.Active);

/// <summary>FOMOD 标记依赖：要求某个安装标记存在或等于指定值。</summary>
/// <param name="Flag">标记名。</param>
/// <param name="Value">期望值（空表示只要存在该标记）。</param>
public sealed record FomodFlagDependency(string Flag, string? Value = null);

/// <summary>
/// FOMOD 复合依赖（对齐 NexusMods <c>CompositeDependency</c>）。
/// 游戏/管理器版本依赖一律保守视为满足（本管理器不维护可比较的版本），因此不建模。
/// </summary>
public sealed record FomodDependency
{
    /// <summary>组合方式。</summary>
    public FomodDependencyOperator Operator { get; init; } = FomodDependencyOperator.And;

    /// <summary>文件依赖。</summary>
    public IReadOnlyList<FomodFileDependency> Files { get; init; } = [];

    /// <summary>标记依赖。</summary>
    public IReadOnlyList<FomodFlagDependency> Flags { get; init; } = [];

    /// <summary>嵌套的复合依赖。</summary>
    public IReadOnlyList<FomodDependency> Nested { get; init; } = [];

    /// <summary>是否没有任何实际约束（仅外部手工构造空依赖时成立；解析器不会产出此类实例）。</summary>
    public bool IsEmpty => Files.Count == 0 && Flags.Count == 0 && Nested.Count == 0;
}

/// <summary>FOMOD 按条件安装的条目模式。</summary>
/// <param name="Dependencies">安装条件。</param>
/// <param name="Files">满足条件时安装的文件。</param>
public sealed record FomodInstallPattern(
    FomodDependency? Dependencies,
    IReadOnlyList<FomodFileMapping> Files);

/// <summary>FOMOD 安装配置（<c>fomod/ModuleConfig.xml</c> 或根目录 <c>ModuleConfig.xml</c>）。</summary>
/// <param name="ModuleName">模块名。</param>
/// <param name="Groups">全部分组（按步骤顺序展开，供旧界面/测试使用）。</param>
public sealed record FomodConfig(string ModuleName, IReadOnlyList<FomodGroup> Groups)
{
    /// <summary>安装步骤。</summary>
    public IReadOnlyList<FomodInstallStep> InstallSteps { get; init; } = [];

    /// <summary>必装文件（无条件安装）。</summary>
    public IReadOnlyList<FomodFileMapping> RequiredInstallFiles { get; init; } = [];

    /// <summary>条件安装规则（按依赖求值）。</summary>
    public IReadOnlyList<FomodInstallPattern> ConditionalInstalls { get; init; } = [];

    /// <summary>模块版本。</summary>
    public string? ModuleVersion { get; init; }

    /// <summary>模块作者。</summary>
    public string? ModuleAuthor { get; init; }

    /// <summary>模块描述。</summary>
    public string? ModuleDescription { get; init; }

    /// <summary>模块预览图（包内相对路径）。</summary>
    public string? ModuleImage { get; init; }

    /// <summary>解析期间被跳过的非法文件映射提示（含字段名与原因）；全部合法时为空集合。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// 解析 FOMOD的 <c>ModuleConfig.xml</c>。
/// </summary>
/// <remarks>
/// <para>覆盖安装所需语义：安装步骤（名称/可见性依赖）、可选分组与插件（类型/说明/图片/标记）、
/// 必装文件、条件安装、模块元数据与优先级。游戏/管理器版本依赖保守视为满足。</para>
/// <para>
/// 路径安全：<c>source</c>/<c>destination</c> 必须是目标根内的安全相对路径。绝对路径
/// （含盘符、UNC、前导分隔符）、含 <c>..</c> 段、空路径的文件映射会被跳过，并记录到
/// <see cref="FomodConfig.Warnings"/>，不会静默丢弃。落盘阶段还会再校验一次（纵深防御）。
/// </para>
/// </remarks>
public static class FomodParser
{
    /// <summary>解析 XML 文本。</summary>
    /// <param name="xml">ModuleConfig.xml 内容。</param>
    /// <param name="packageFiles">
    /// 包内全部文件（相对路径）；用于展开 <c>&lt;folder&gt;</c> 映射。为 <see langword="null"/> 时
    /// 文件夹映射会被跳过并记录 warning。
    /// </param>
    /// <returns>解析结果；格式非法或没有任何安装内容时返回 <see langword="null"/>。</returns>
    public static FomodConfig? Parse(string? xml, IReadOnlyList<string>? packageFiles = null)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        XElement? root = document.Root;
        if (root is null)
        {
            return null;
        }

        List<string> warnings = [];
        List<string> availableFiles = CreateAvailableFileIndex(packageFiles);

        string moduleName = ChildValue(root, "moduleName") ?? "FOMOD";
        List<FomodFileMapping> required = ParseFileList(
            FindChild(root, "requiredInstallFiles"), "requiredInstallFiles", warnings, availableFiles);
        List<FomodInstallPattern> conditional = ParseConditionalInstalls(root, warnings, availableFiles);

        List<FomodInstallStep> steps = [];
        foreach (XElement stepElement in FindChildren(root, "installSteps", "installStep"))
        {
            string stepName = Attribute(stepElement, "name") ?? "安装步骤";
            FomodDependency? visible = ParseDependency(FindChild(stepElement, "visible"));
            List<FomodGroup> stepGroups = [];
            foreach (XElement groupElement in FindGroups(stepElement))
            {
                List<FomodPlugin> plugins = [];
                foreach (XElement pluginElement in FindPlugins(groupElement))
                {
                    plugins.Add(ParsePlugin(pluginElement, warnings, availableFiles));
                }

                if (plugins.Count == 0)
                {
                    continue;
                }

                stepGroups.Add(new FomodGroup(
                    Attribute(groupElement, "name") ?? "选项",
                    ParseGroupType(Attribute(groupElement, "type")),
                    plugins));
            }

            steps.Add(new FomodInstallStep(stepName, visible, stepGroups));
        }

        List<FomodGroup> groups = [.. steps.SelectMany(s => s.Groups)];
        bool hasContent = groups.Count > 0 || required.Count > 0 || conditional.Count > 0;
        if (!hasContent)
        {
            return null;
        }

        return new FomodConfig(moduleName, groups)
        {
            InstallSteps = steps,
            RequiredInstallFiles = required,
            ConditionalInstalls = conditional,
            ModuleVersion = ChildValue(root, "version"),
            ModuleAuthor = ChildValue(root, "author"),
            ModuleDescription = ChildValue(root, "description"),
            ModuleImage = ReadImagePath(FindChild(root, "moduleImage")),
            Warnings = warnings,
        };
    }

    /// <summary>解析给定文本（配置文件路径仅用于定位，不写入结果）。</summary>
    /// <param name="xml">XML 文本。</param>
    /// <param name="configPath">配置文件包内相对路径。</param>
    /// <param name="packageFiles">包内文件清单。</param>
    /// <returns>解析结果。</returns>
    public static FomodConfig? Parse(string? xml, string configPath, IReadOnlyList<string>? packageFiles)
    {
        _ = configPath;
        return Parse(xml, packageFiles);
    }

    private static FomodPlugin ParsePlugin(
        XElement pluginElement,
        List<string> warnings,
        List<string> availableFiles)
    {
        string pluginName = Attribute(pluginElement, "name") ?? "未命名选项";
        List<FomodFileMapping> files = ParseFileList(
            FindChild(pluginElement, "files"), pluginName, warnings, availableFiles);
        List<FomodFlag> flags = [];
        XElement? flagContainer = FindChild(pluginElement, "conditionFlags");
        if (flagContainer is not null)
        {
            foreach (XElement flagElement in flagContainer
                .Descendants()
                .Where(e => e.Name.LocalName.Equals("flag", StringComparison.OrdinalIgnoreCase)))
            {
                string? name = Attribute(flagElement, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                flags.Add(new FomodFlag(name, Attribute(flagElement, "value")));
            }
        }

        return new FomodPlugin(
            pluginName,
            ChildValue(pluginElement, "description"),
            files,
            ParsePluginType(pluginElement),
            ReadImagePath(FindChild(pluginElement, "image")),
            flags);
    }

    private static List<FomodInstallPattern> ParseConditionalInstalls(
        XElement root,
        List<string> warnings,
        List<string> availableFiles)
    {
        List<FomodInstallPattern> patterns = [];
        XElement? container = FindChild(root, "conditionalFileInstalls");
        if (container is null)
        {
            return patterns;
        }

        foreach (XElement pattern in container
            .Descendants()
            .Where(e => e.Name.LocalName.Equals("pattern", StringComparison.OrdinalIgnoreCase)))
        {
            FomodDependency? dependencies = ParseDependency(FindChild(pattern, "dependencies"));
            List<FomodFileMapping> files = ParseFileList(
                FindChild(pattern, "files"), "conditionalFileInstalls", warnings, availableFiles);
            if (files.Count > 0)
            {
                patterns.Add(new FomodInstallPattern(dependencies, files));
            }
        }

        return patterns;
    }

    /// <summary>解析 <c>files</c>/<c>requiredInstallFiles</c> 容器下的 file/folder 元素。</summary>
    private static List<FomodFileMapping> ParseFileList(
        XElement? container,
        string context,
        List<string> warnings,
        List<string> availableFiles)
    {
        List<FomodFileMapping> mappings = [];
        if (container is null)
        {
            return mappings;
        }

        foreach (XElement element in container.Elements())
        {
            string localName = element.Name.LocalName;
            bool isFolder = localName.Equals("folder", StringComparison.OrdinalIgnoreCase);
            if (!isFolder && !localName.Equals("file", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ParseFileElement(element, context, isFolder, warnings, availableFiles, mappings);
        }

        return mappings;
    }

    private static void ParseFileElement(
        XElement element,
        string context,
        bool isFolder,
        List<string> warnings,
        List<string> availableFiles,
        List<FomodFileMapping> mappings)
    {
        string? rawSource = Attribute(element, "source");
        string? rawDestination = Attribute(element, "destination");

        // 两条路径都要先校验：即使 source 已非法，也要把 destination的问题一起报出来。
        bool sourceOk = TryNormalizeSafePath(rawSource, "source", context, warnings, out string safeSource);
        bool destinationOk = TryNormalizeSafePath(
            rawDestination, "destination", context, warnings, out string safeDestination);
        int priority = ParsePriority(Attribute(element, "priority"));
        if (!sourceOk || !destinationOk)
        {
            return;
        }

        if (!isFolder)
        {
            mappings.Add(new FomodFileMapping(safeSource, safeDestination, priority));
            return;
        }

        // folder：把该目录下所有文件按 <destination>/<相对路径> 展开。
        string folderPrefix = safeSource.TrimEnd('/') + "/";
        List<FomodFileMapping> expanded = [];
        foreach (string file in availableFiles)
        {
            if (!file.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string remaining = file[folderPrefix.Length..];
            if (remaining.Length == 0)
            {
                continue;
            }

            string destination = safeDestination.TrimEnd('/');
            expanded.Add(new FomodFileMapping(
                file,
                destination.Length == 0 ? remaining : destination + "/" + remaining,
                priority));
        }

        if (expanded.Count == 0)
        {
            warnings.Add($"跳过 FOMOD 文件夹映射（{context} source=\"{rawSource}\"）：包内没有对应文件");
            return;
        }

        mappings.AddRange(expanded);
    }

    private static List<string> CreateAvailableFileIndex(IReadOnlyList<string>? packageFiles)
    {
        if (packageFiles is null)
        {
            return [];
        }

        List<string> normalized = [];
        foreach (string file in packageFiles)
        {
            if (string.IsNullOrWhiteSpace(file))
            {
                continue;
            }

            try
            {
                normalized.Add(Normalize(file));
            }
            catch (ArgumentException)
            {
                // 包内路径在识别阶段已经规范化，这里仅防御。
            }
        }

        return normalized;
    }

    private static IEnumerable<XElement> FindChildren(XElement parent, string containerName, string childName)
    {
        XElement? container = FindChild(parent, containerName);
        if (container is null)
        {
            yield break;
        }

        foreach (XElement child in container.Elements())
        {
            if (child.Name.LocalName.Equals(childName, StringComparison.OrdinalIgnoreCase))
            {
                yield return child;
            }
        }
    }

    private static IEnumerable<XElement> FindGroups(XElement stepElement)
    {
        XElement? container = FindChild(stepElement, "optionalFileGroups");
        if (container is null)
        {
            yield break;
        }

        foreach (XElement group in container.Elements())
        {
            if (group.Name.LocalName.Equals("group", StringComparison.OrdinalIgnoreCase))
            {
                yield return group;
            }
        }
    }

    private static IEnumerable<XElement> FindPlugins(XElement groupElement)
    {
        XElement? container = FindChild(groupElement, "plugins");
        if (container is null)
        {
            yield break;
        }

        foreach (XElement plugin in container.Elements())
        {
            if (plugin.Name.LocalName.Equals("plugin", StringComparison.OrdinalIgnoreCase))
            {
                yield return plugin;
            }
        }
    }

    private static FomodDependency? ParseDependency(XElement? element)
    {
        if (element is null)
        {
            return null;
        }

        List<FomodFileDependency> files = [];
        List<FomodFlagDependency> flags = [];
        List<FomodDependency> nested = [];
        foreach (XElement child in element.Elements())
        {
            switch (child.Name.LocalName.ToLowerInvariant())
            {
                case "filedependency":
                    {
                        string? file = Attribute(child, "file");
                        if (!string.IsNullOrWhiteSpace(file))
                        {
                            files.Add(new FomodFileDependency(file, ParseFileState(Attribute(child, "state"))));
                        }

                        break;
                    }

                case "flagdependency":
                    {
                        string? flag = Attribute(child, "flag");
                        if (!string.IsNullOrWhiteSpace(flag))
                        {
                            flags.Add(new FomodFlagDependency(flag, Attribute(child, "value")));
                        }

                        break;
                    }

                case "dependencies":
                    {
                        FomodDependency? childDependency = ParseDependency(child);
                        if (childDependency is not null && !childDependency.IsEmpty)
                        {
                            nested.Add(childDependency);
                        }

                        break;
                    }

                default:
                    // gameDependency / fommDependency：保守视为满足（默认行为）。
                    break;
            }
        }

        if (files.Count == 0 && flags.Count == 0 && nested.Count == 0)
        {
            return null;
        }

        string? operatorName = Attribute(element, "operator");
        return new FomodDependency
        {
            Operator = operatorName?.Equals("Or", StringComparison.OrdinalIgnoreCase) == true
                ? FomodDependencyOperator.Or
                : FomodDependencyOperator.And,
            Files = files,
            Flags = flags,
            Nested = nested,
        };
    }

    private static FomodFileState ParseFileState(string? state) => state?.ToLowerInvariant() switch
    {
        "inactive" => FomodFileState.Inactive,
        "missing" => FomodFileState.Missing,
        _ => FomodFileState.Active,
    };

    private static FomodGroupType ParseGroupType(string? type) => type?.ToLowerInvariant() switch
    {
        "selectexactlyone" => FomodGroupType.SelectExactlyOne,
        "selectatmostone" => FomodGroupType.SelectAtMostOne,
        "selectall" => FomodGroupType.SelectAll,
        "selectatleastone" => FomodGroupType.SelectAtLeastOne,
        _ => FomodGroupType.SelectAny,
    };

    private static FomodPluginType ParsePluginType(XElement pluginElement)
    {
        XElement? descriptor = FindChild(pluginElement, "typeDescriptor");
        if (descriptor is null)
        {
            return FomodPluginType.Optional;
        }

        XElement? type = descriptor
            .Descendants()
            .FirstOrDefault(e => e.Name.LocalName.Equals("type", StringComparison.OrdinalIgnoreCase));
        string? name = type is null ? null : Attribute(type, "name") ?? type.Value.Trim();
        return name?.ToLowerInvariant() switch
        {
            "required" => FomodPluginType.Required,
            "recommended" => FomodPluginType.Recommended,
            "notusable" => FomodPluginType.NotUsable,
            "couldbeusable" => FomodPluginType.CouldBeUsable,
            _ => FomodPluginType.Optional,
        };
    }

    private static int ParsePriority(string? raw) =>
        int.TryParse(raw, out int priority) ? priority : 0;

    private static string? ReadImagePath(XElement? element)
    {
        if (element is null)
        {
            return null;
        }

        string? path = Attribute(element, "path") ?? element.Value.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Normalize(path);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static XElement? FindChild(XElement element, string name) =>
        element.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>取子元素文本并去掉首尾空白；元素不存在或去掉后为空时返回 <see langword="null"/>。</summary>
    private static string? ChildValue(XElement element, string name)
    {
        string? value = FindChild(element, name)?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string? Attribute(XElement element, string name) =>
        element.Attributes()
            .FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?.Value
            .Trim();

    /// <summary>
    /// 校验 FOMOD的 source/destination 是否是安全相对路径；合法时输出规范化结果，非法时写 warning。
    /// </summary>
    private static bool TryNormalizeSafePath(
        string? value,
        string field,
        string pluginName,
        List<string> warnings,
        out string normalized)
    {
        normalized = string.Empty;
        string? reason = ClassifyUnsafePath(value);
        if (reason is not null)
        {
            warnings.Add($"跳过非法 FOMOD 文件映射（插件「{pluginName}」{field}=\"{value}\"）：{reason}");
            return false;
        }

        normalized = Normalize(value!);
        return true;
    }

    /// <summary>返回非法原因；安全相对路径返回 <see langword="null"/>。</summary>
    private static string? ClassifyUnsafePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "路径为空";
        }

        string trimmed = value.Trim();
        string slashed = trimmed.Replace('\\', '/');

        // 前导 /（含 //server/share 形式）、盘符以及平台判定的根路径都算绝对路径
        if (slashed.StartsWith('/') || Path.IsPathRooted(trimmed))
        {
            return "不允许绝对路径";
        }

        // 冒号：盘符（C:）或 NTFS 备用数据流（file.txt:stream）
        if (slashed.Contains(':'))
        {
            return "不允许包含冒号（盘符或备用数据流）";
        }

        string[] segments = slashed.Split('/');
        if (segments.Contains(".."))
        {
            return "不允许包含 .. 路径段";
        }

        bool hasContent = segments.Any(s => s.Length > 0 && s != ".");
        return hasContent ? null : "规范化后为空路径";
    }

    /// <summary>
    /// 把安全相对路径统一成「正斜杠、去首尾空白（段内空格保留）、无空段、无 <c>.</c> 段、小写」的形式。
    /// 旧行为（大小写统一为小写）保留，避免影响既有解析结果与安装落盘。
    /// </summary>
    private static string Normalize(string path)
    {
        string[] segments = path.Replace('\\', '/').Trim().Split('/');
        return string.Join('/', segments.Where(s => s.Length > 0 && s != ".")).ToLowerInvariant();
    }
}
