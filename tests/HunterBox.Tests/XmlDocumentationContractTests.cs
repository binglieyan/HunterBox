using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using HunterBox.Application.Common;
using HunterBox.Application.Mount;
using HunterBox.Application.Paks;
using HunterBox.Application.Saves;
using HunterBox.Application.Settings;
using HunterBox.Domain.Archives;
using HunterBox.Domain.Mods;

namespace HunterBox.Tests;

/// <summary>
/// XML 文档契约测试：CS1591 只能保证“有注释”，这里进一步校验关键接口的
/// summary/param/returns/exception 结构与安全承诺，防止注释与实现再次脱节。
/// </summary>
public class XmlDocumentationContractTests
{
    private static readonly Type[] KeyInterfaces =
    [
        typeof(IFileSystem),
        typeof(IArchiveExtractor),
        typeof(IPakArchiveReader),
        typeof(IBackupManifestStore),
        typeof(IUndoScope),
        typeof(IModStore),
        typeof(ISettings),
        typeof(IPakMountService),
        typeof(IPakRepacker),
        typeof(ILooseFileMountService),
        typeof(IGameInstallProbe),
    ];

    /// <summary>关键接口的每个成员都必须有 summary；方法参数与返回值必须逐项有文档。</summary>
    [Fact]
    public void KeyInterfaces_AllMembersHaveSummaryParamsAndReturns()
    {
        List<string> offenders = [];
        foreach (Type type in KeyInterfaces)
        {
            XDocument document = LoadDocumentation(type);
            foreach (MemberInfo member in type.GetMembers(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (member is MethodInfo { IsSpecialName: true })
                {
                    continue;
                }

                if (member is not MethodInfo and not PropertyInfo and not EventInfo)
                {
                    continue;
                }

                XElement? element = FindMember(document, type, member);
                if (element is null)
                {
                    offenders.Add($"{type.FullName}.{member.Name}: 缺少 XML 成员文档");
                    continue;
                }

                XElement? summary = element.Element("summary");
                if (summary is null || (string.IsNullOrWhiteSpace(summary.Value) && summary.Element("inheritdoc") is null))
                {
                    offenders.Add($"{type.FullName}.{member.Name}: summary 为空");
                }

                if (member is not MethodInfo method)
                {
                    continue;
                }

                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    if (element.Elements("param").All(
                        item => !string.Equals((string?)item.Attribute("name"), parameter.Name, StringComparison.Ordinal)))
                    {
                        offenders.Add($"{type.FullName}.{method.Name}: 缺少 param name=\"{parameter.Name}\"");
                    }
                }

                if (method.ReturnType != typeof(void) && element.Element("returns") is null)
                {
                    offenders.Add($"{type.FullName}.{method.Name}: 缺少 returns");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "关键接口文档不完整：" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>IFileSystem 的 Try 系列必须明确“失败返回 false”，避免调用方误按抛异常处理。</summary>
    [Fact]
    public void IFileSystem_TryMethods_DocumentFalseFailureContract()
    {
        XDocument document = LoadDocumentation(typeof(IFileSystem));
        List<string> offenders = [];
        foreach (MethodInfo method in typeof(IFileSystem).GetMethods()
            .Where(method => method.Name.StartsWith("Try", StringComparison.Ordinal)))
        {
            XElement element = RequiredMember(document, typeof(IFileSystem), method);
            string text = element.ToString();
            if (text.IndexOf("false", StringComparison.OrdinalIgnoreCase) < 0
                && text.IndexOf("失败", StringComparison.Ordinal) < 0)
            {
                offenders.Add($"{method.Name}: 未说明失败返回 false");
            }
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    /// <summary>严格删除方法必须声明 IOException，提醒调用方处理失败。</summary>
    [Fact]
    public void IFileSystem_StrictDeletes_DocumentIOException()
    {
        XDocument document = LoadDocumentation(typeof(IFileSystem));
        string[] names = ["DeleteFile", "DeleteDirectory", "DeleteEmptyDirectory"];
        List<string> offenders = [];
        foreach (string name in names)
        {
            MethodInfo method = typeof(IFileSystem).GetMethod(name)
                ?? throw new InvalidOperationException($"IFileSystem 缺少方法 {name}");
            XElement element = RequiredMember(document, typeof(IFileSystem), method);
            bool documentsIo = element.Elements("exception").Any(
                exception => ((string?)exception.Attribute("cref"))?.Contains("IOException", StringComparison.Ordinal) == true
                    || exception.Value.Contains("IOException", StringComparison.Ordinal));
            if (!documentsIo)
            {
                offenders.Add($"{name}: 未声明 IOException");
            }
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    /// <summary>强制删除方法必须说明链接安全语义（只删链接本身/子树含链接整体跳过）。</summary>
    [Fact]
    public void IFileSystem_ForceDeletes_DocumentLinkSafety()
    {
        XDocument document = LoadDocumentation(typeof(IFileSystem));
        string[] names = ["TryDeleteFileForce", "TryDeleteDirectoryForce"];
        List<string> offenders = [];
        foreach (string name in names)
        {
            MethodInfo method = typeof(IFileSystem).GetMethod(name)
                ?? throw new InvalidOperationException($"IFileSystem 缺少方法 {name}");
            XElement element = RequiredMember(document, typeof(IFileSystem), method);
            if (!element.Value.Contains("链接", StringComparison.Ordinal))
            {
                offenders.Add($"{name}: 未说明链接处理语义");
            }
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    /// <summary>Domain/Application 的 public 类型与成员都必须有非空 summary。</summary>
    [Fact]
    public void PublicTypes_AllMembersHaveSummary()
    {
        List<string> offenders = [];
        Assembly[] assemblies = [typeof(IFileSystem).Assembly, typeof(IModStore).Assembly];
        foreach (Assembly assembly in assemblies)
        {
            XDocument document = LoadDocumentation(assembly);
            XElement members = document.Root?.Element("members")
                ?? throw new InvalidOperationException($"XML 文档缺少 members 根节点：{assembly.GetName().Name}");
            foreach (Type type in assembly.GetExportedTypes())
            {
                CheckMemberSummary(members, $"T:{type.FullName}", type.FullName ?? type.Name, offenders);
                foreach (MemberInfo member in type.GetMembers(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (member is not MethodInfo and not PropertyInfo and not EventInfo and not FieldInfo)
                    {
                        continue;
                    }

                    // 枚举的 value__ 由编译器生成，没有也不需要有文档。
                    if (member is FieldInfo { Name: "value__" })
                    {
                        continue;
                    }

                    if (member.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
                    {
                        continue;
                    }

                    if (member is MethodInfo { IsSpecialName: true })
                    {
                        continue;
                    }

                    // 委托的 Invoke/BeginInvoke/EndInvoke 由运行时提供，源码里没有对应声明。
                    if (typeof(Delegate).IsAssignableFrom(type) && member is MethodInfo)
                    {
                        continue;
                    }

                    XElement? element = FindMember(document, type, member);
                    string name = $"{type.FullName}.{member.Name}";
                    if (element is null)
                    {
                        offenders.Add($"{name}: 缺少 XML 成员文档");
                        continue;
                    }

                    if (!HasSummary(element))
                    {
                        offenders.Add($"{name}: summary 为空");
                    }
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "public 类型文档不完整：" + Environment.NewLine
                + string.Join(Environment.NewLine, offenders.Distinct(StringComparer.Ordinal)));
    }

    /// <summary>所有公开 XML 文档不得出现空 summary 或占位文本。</summary>
    [Fact]
    public void PublicXmlDocuments_HaveNoEmptySummaryOrPlaceholder()
    {
        Type[] anchors =
        [
            typeof(IFileSystem),
            typeof(IArchiveExtractor),
            typeof(HunterBox.Infrastructure.IO.SystemFileSystem),
        ];
        string[] placeholders = ["TODO", "FIXME", "待补充", "lorem ipsum"];
        List<string> offenders = [];
        foreach (Type anchor in anchors)
        {
            XDocument document = LoadDocumentation(anchor);
            foreach (XElement member in document.Root?.Element("members")?.Elements("member") ?? [])
            {
                // CS1591 负责“有文档”；这里只检查已存在的 summary 是否为空或占位，
                // 记录类型的主构造函数等编译器生成成员没有 summary 属于正常情况。
                XElement? summary = member.Element("summary");
                if (summary is null || member.Element("inheritdoc") is not null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(summary.Value) && summary.Element("inheritdoc") is null)
                {
                    offenders.Add($"{member.Attribute("name")?.Value}: summary 为空");
                }

                if (placeholders.Any(
                    placeholder => summary.Value.Contains(placeholder, StringComparison.OrdinalIgnoreCase)))
                {
                    offenders.Add($"{member.Attribute("name")?.Value}: summary 含占位文本");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "XML 文档存在空 summary/占位文本：" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    private static XDocument LoadDocumentation(Type type) => LoadDocumentation(type.Assembly);

    private static void CheckMemberSummary(
        XElement members,
        string memberName,
        string displayName,
        List<string> offenders)
    {
        XElement? element = members.Elements("member")
            .FirstOrDefault(item => string.Equals((string?)item.Attribute("name"), memberName, StringComparison.Ordinal));
        if (element is null)
        {
            offenders.Add($"{displayName}: 缺少类型 XML 文档");
            return;
        }

        if (!HasSummary(element))
        {
            offenders.Add($"{displayName}: 类型 summary 为空");
        }
    }

    private static bool HasSummary(XElement element)
    {
        XElement? summary = element.Element("summary");
        return (summary is not null && !string.IsNullOrWhiteSpace(summary.Value))
            || summary?.Element("inheritdoc") is not null
            || element.Element("inheritdoc") is not null;
    }

    private static XDocument LoadDocumentation(Assembly assembly)
    {
        string path = Path.ChangeExtension(assembly.Location, ".xml");
        Assert.True(File.Exists(path), $"缺少 XML 文档文件：{path}");
        return XDocument.Load(path);
    }

    private static XElement RequiredMember(XDocument document, Type type, MemberInfo member) =>
        FindMember(document, type, member)
        ?? throw new InvalidOperationException($"缺少 XML 成员文档：{type.FullName}.{member.Name}");

    private static XElement? FindMember(XDocument document, Type type, MemberInfo member)
    {
        // 按完整签名（参数类型 + 泛型元数）生成文档成员名，无精确命中即视为缺文档。
        string expected = member switch
        {
            MethodInfo methodInfo => MethodDocumentationName(type, methodInfo),
            PropertyInfo property => $"P:{type.FullName}.{property.Name}",
            EventInfo @event => $"E:{type.FullName}.{@event.Name}",
            FieldInfo field => $"F:{type.FullName}.{field.Name}",
            _ => $"M:{type.FullName}.{member.Name}",
        };

        return (document.Root?.Element("members")?.Elements("member") ?? [])
            .FirstOrDefault(element => string.Equals(
                (string?)element.Attribute("name"),
                expected,
                StringComparison.Ordinal));
    }

    /// <summary>生成方法的 XML 文档成员名（含泛型元数与参数类型）。</summary>
    /// <param name="type">声明类型。</param>
    /// <param name="method">方法。</param>
    /// <returns>文档成员名。</returns>
    private static string MethodDocumentationName(Type type, MethodInfo method)
    {
        string name = "M:" + type.FullName + "." + method.Name;
        if (method.IsGenericMethodDefinition)
        {
            name += "``" + method.GetGenericArguments().Length.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        }

        ParameterInfo[] parameters = method.GetParameters();
        return parameters.Length == 0
            ? name
            : name + "(" + string.Join(",", parameters.Select(parameter => XmlTypeName(parameter.ParameterType))) + ")";
    }

    /// <summary>把反射类型名转换成 XML 文档成员名里的类型写法。</summary>
    /// <param name="type">参数类型。</param>
    /// <returns>XML 类型名。</returns>
    private static string XmlTypeName(Type type)
    {
        if (type.IsByRef)
        {
            return XmlTypeName(type.GetElementType()!) + "@";
        }

        if (type.IsPointer)
        {
            return XmlTypeName(type.GetElementType()!) + "*";
        }

        if (type.IsArray)
        {
            Type element = type.GetElementType()!;
            return type == element.MakeArrayType()
                ? XmlTypeName(element) + "[]"
                : XmlTypeName(element) + "[0:,0:]";
        }

        if (type.IsGenericParameter)
        {
            string prefix = type.DeclaringMethod is null ? "`" : "``";
            return prefix + type.GenericParameterPosition.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        }

        if (type.IsGenericType)
        {
            string definition = StripGenericArity((type.GetGenericTypeDefinition().FullName ?? type.Name).Replace('+', '.'));
            return definition + "{" + string.Join(",", type.GetGenericArguments().Select(XmlTypeName)) + "}";
        }

        return (type.FullName ?? type.Name).Replace('+', '.');
    }

    /// <summary>去掉反射类型名里的 <c>`n</c> 元数标记（XML 文档名用 <c>{...}</c> 表示泛型实参）。</summary>
    /// <param name="name">反射类型名。</param>
    /// <returns>去掉元数的类型名。</returns>
    private static string StripGenericArity(string name)
    {
        System.Text.StringBuilder builder = new(name.Length);
        for (int i = 0; i < name.Length; i++)
        {
            if (name[i] != '`')
            {
                builder.Append(name[i]);
                continue;
            }

            i++;
            while (i < name.Length && char.IsDigit(name[i]))
            {
                i++;
            }

            i--;
        }

        return builder.ToString();
    }
}
