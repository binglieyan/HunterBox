using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HunterBox.Tests;

/// <summary>
/// XAML 引用守卫：Roslyn 看不到 XAML 按名称调用的事件处理器，IDE0051 在 App 层被降为 suggestion。
/// 这里按 <c>x:Class</c> 校验每个 XAML 事件属性都能在对应分部类里找到方法，避免清理死代码时误删事件处理器。
/// </summary>
public partial class XamlReferenceGuardTests
{
    /// <summary>所有 XAML 事件处理器都必须有对应类里的 C# 方法。</summary>
    [SourceTreeFact]
    public void XamlEventHandlers_HaveMatchingMethods()
    {
        string root = SourceTreeLocator.RootOrThrow();
        string appRoot = Path.Combine(root, "src", "HunterBox.App");
        Dictionary<string, HashSet<string>> methods = CollectMethodsByClass();
        List<string> offenders = [];
        List<string> unknownEvents = [];

        foreach (string xaml in SourceTreeLocator.EnumerateRepositoryFiles()
            .Where(file => file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                && file.StartsWith(appRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
        {
            string relative = Path.GetRelativePath(root, xaml);
            // 仓库级枚举已剪掉 obj/bin，这里无需再过滤构建产物。
            string text = File.ReadAllText(xaml);
            Match classMatch = ClassPattern().Match(text);
            if (!classMatch.Success)
            {
                continue;
            }

            string fullName = classMatch.Groups["class"].Value;
            if (!methods.TryGetValue(fullName, out HashSet<string>? classMethods))
            {
                offenders.Add($"{relative}: 找不到 x:Class={fullName} 对应的 C# 类");
                continue;
            }

            foreach (Match match in AttributePattern().Matches(text))
            {
                string attribute = match.Groups["attribute"].Value;
                string handler = match.Groups["value"].Value;
                // 事件处理器支持 Class.Method 形式，按最后一段匹配类内方法。
                string handlerName = HandlerMethodName(handler);
                if (!IsEventAttribute(attribute))
                {
                    // 未知但以 Event 结尾的属性进入可断言清单；
                    // 属性值恰好是类内方法名（含点号形式）时也视为「疑似未识别事件」。
                    if (attribute.EndsWith("Event", StringComparison.Ordinal) || classMethods.Contains(handlerName))
                    {
                        unknownEvents.Add($"{relative}: {attribute}=\"{handler}\"");
                    }

                    continue;
                }

                if (!classMethods.Contains(handlerName))
                {
                    offenders.Add($"{relative}: 事件处理器 {handler} 不在 {fullName} 中");
                }
            }
        }

        Assert.True(
            unknownEvents.Count == 0,
            "存在未登记的事件属性（请加入 KnownEventNames 或改用已知事件名）："
            + Environment.NewLine + string.Join(Environment.NewLine, unknownEvents));
        Assert.True(
            offenders.Count == 0,
            "XAML 事件处理器缺失：" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// 反向检查：像事件处理器的方法如果既不被 XAML 引用、也不在 C# 中引用，直接失败。
    /// </summary>
    /// <remarks>
    /// 事件处理器可能通过代码订阅（<c>button.Click += OnClick</c>），这里的代码侧引用只认
    /// <c>+=</c>/<c>-=</c> 的方法组（<c>nameof</c>/普通标识符不算引用）；
    /// 两个来源都没有的事件形方法视为死代码/漏引用，必须失败而不是只写日志。
    /// </remarks>
    [SourceTreeFact]
    public void EventLikeMethods_WithoutAnyReference_Fail()
    {
        string root = SourceTreeLocator.RootOrThrow();
        string appRoot = Path.Combine(root, "src", "HunterBox.App");
        HashSet<string> referencedInXaml = new(StringComparer.Ordinal);

        foreach (string xaml in SourceTreeLocator.EnumerateRepositoryFiles()
            .Where(file => file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                && file.StartsWith(appRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
        {
            // 仓库级枚举已剪掉 obj/bin，这里无需再过滤构建产物。
            foreach (Match match in AttributePattern().Matches(File.ReadAllText(xaml)))
            {
                if (IsEventAttribute(match.Groups["attribute"].Value))
                {
                    // Class.Method 形式按最后一段记录，反向检查才能匹配类内方法。
                    referencedInXaml.Add(HandlerMethodName(match.Groups["value"].Value));
                }
            }
        }

        // 复用分层门禁的 App 语法树缓存，整个测试进程只解析一次。
        // 代码侧只认事件订阅（+=/-= 方法组），nameof/普通标识符不再算处理器引用。
        HashSet<string> referencedInCode = CollectCodeEventSubscriptions(SourceTreeCompilationCache.Get("HunterBox.App").Trees);
        List<SyntaxTree> trees = [.. SourceTreeCompilationCache.Get("HunterBox.App").Trees];

        List<string> offenders = [];
        foreach (SyntaxTree tree in trees)
        {
            foreach (MethodDeclarationSyntax method in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                string name = method.Identifier.ValueText;
                if (!(LooksLikeEventHandlerName(name) || HasEventHandlerShape(method))
                    || referencedInXaml.Contains(name)
                    || referencedInCode.Contains(name))
                {
                    continue;
                }

                offenders.Add($"{Path.GetFileName(tree.FilePath)}: {name} 形似事件处理器但未被 XAML/C# 引用");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "事件形方法未被任何地方引用：" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>ClickMode 枚举属性不是事件；自定义事件按属性名后缀识别。</summary>
    [Fact]
    public void IsEventAttribute_ClickModeAndCustomHandler()
    {
        Assert.True(IsEventAttribute("Click"));
        Assert.True(IsEventAttribute("Drop"));
        Assert.True(IsEventAttribute("CustomClick"));
        Assert.False(IsEventAttribute("ClickMode"));
        Assert.False(IsEventAttribute("x:Name"));

        Assert.True(IsEventAttribute("CustomClick"));
        Assert.True(IsEventAttribute("IsEnabledChanged"));
        // IsHitTestVisibleChanged 这类真实事件不能再因 Is* 前缀被静默跳过。
        Assert.True(IsEventAttribute("IsHitTestVisibleChanged"));
        // 非 Changed 后缀的常见焦点事件也必须识别。
        Assert.True(IsEventAttribute("GotFocus"));
        Assert.True(IsEventAttribute("LostFocus"));
        // 状态属性不算事件。
        Assert.False(IsEventAttribute("AllowDrop"));
        Assert.False(IsEventAttribute("IsChecked"));
        Assert.False(IsEventAttribute("ClickMode"));
        Assert.False(IsEventAttribute("Width"));
    }

    /// <summary>事件处理器签名形状（object + EventArgs 派生参数，含限定类型）可由语法识别。</summary>
    [Fact]
    public void HasEventHandlerShape_RequiresSenderAndEventArgs()
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText("""
            using System;
            class C
            {
                void OnClick(object sender, EventArgs e) { }
                void Qualified(object sender, System.EventArgs e) { }
                void Wrong(object sender, int e) { }
                void One(object sender) { }
            }
            """);
        MethodDeclarationSyntax[] methods = [.. tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()];

        Assert.True(HasEventHandlerShape(methods[0]));
        Assert.True(HasEventHandlerShape(methods[1]));
        Assert.False(HasEventHandlerShape(methods[2]));
        Assert.False(HasEventHandlerShape(methods[3]));
    }

    /// <summary>源码树缺失时门禁不能整组静默跳过。</summary>
    [Fact]
    public void SourceTree_IsAvailableForGuards() =>
        Assert.NotNull(SourceTreeLocator.TryFind());

    /// <summary>代码侧只认 <c>+=</c>/<c>-=</c> 订阅的方法组。</summary>
    /// <param name="trees">App 源码语法树。</param>
    /// <returns>被事件订阅引用的方法名。</returns>
    private static HashSet<string> CollectCodeEventSubscriptions(IEnumerable<SyntaxTree> trees)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (SyntaxTree tree in trees)
        {
            foreach (AssignmentExpressionSyntax assignment in
                tree.GetRoot().DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (!assignment.IsKind(SyntaxKind.AddAssignmentExpression)
                    && !assignment.IsKind(SyntaxKind.SubtractAssignmentExpression))
                {
                    continue;
                }

                // 右侧可能是方法组、this.OnClick 或 new Handler(OnClick)，统一收集其中的标识符。
                foreach (IdentifierNameSyntax identifier in
                    assignment.Right.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
                {
                    names.Add(identifier.Identifier.ValueText);
                }
            }
        }

        return names;
    }

    /// <summary>事件处理器名支持 <c>Class.Method</c> 形式，返回最后一段。</summary>
    /// <param name="handler">XAML 属性值。</param>
    /// <returns>方法名。</returns>
    private static string HandlerMethodName(string handler)
    {
        int dot = handler.LastIndexOf('.');
        return dot >= 0 ? handler[(dot + 1)..] : handler;
    }

    /// <summary>点号处理器取最后一段；普通委托合并/new Handler(...)/this.OnClick 都算订阅引用。</summary>
    [Fact]
    public void HandlerReferences_OnlyCountEventSubscriptions()
    {
        Assert.Equal("OnClick", HandlerMethodName("This.OnClick"));
        Assert.Equal("OnClick", HandlerMethodName("OnClick"));

        SyntaxTree tree = CSharpSyntaxTree.ParseText("""
            using System;
            class C
            {
                void OnClick(object sender, EventArgs e) { }
                void Wire(Action<object, EventArgs> handler)
                {
                    handler += OnClick;
                    handler += this.OnClick;
                    handler += new EventHandler<object>(OnClick);
                }

                string Name() => nameof(OnClick);
                Action<object, EventArgs> Alias() => OnClick;
            }
            """);

        HashSet<string> subscriptions = CollectCodeEventSubscriptions([tree]);

        Assert.Contains("OnClick", subscriptions);
        Assert.Single(subscriptions);
    }

    private static Dictionary<string, HashSet<string>> CollectMethodsByClass()
    {
        Dictionary<string, HashSet<string>> classes = new(StringComparer.Ordinal);
        // 复用分层门禁的 App 语法树缓存，App 源码整个测试进程只解析一次。
        foreach (SyntaxTree tree in SourceTreeCompilationCache.Get("HunterBox.App").Trees)
        {
            foreach (ClassDeclarationSyntax classNode in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                string name = FullClassName(classNode);
                if (!classes.TryGetValue(name, out HashSet<string>? methods))
                {
                    methods = new HashSet<string>(StringComparer.Ordinal);
                    classes[name] = methods;
                }

                foreach (MethodDeclarationSyntax method in classNode.Members.OfType<MethodDeclarationSyntax>())
                {
                    methods.Add(method.Identifier.ValueText);
                }
            }
        }

        return classes;
    }

    /// <summary>类的全名（命名空间 + 类名）；XAML 的 <c>x:Class</c> 与之一一对应。</summary>
    private static string FullClassName(ClassDeclarationSyntax classNode)
    {
        string? namespaceName = classNode.Ancestors()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .FirstOrDefault()?.Name.ToString();
        return string.IsNullOrEmpty(namespaceName)
            ? classNode.Identifier.ValueText
            : namespaceName + "." + classNode.Identifier.ValueText;
    }

    /// <summary>全名映射的自测：不同命名空间的同名类不会互相掩盖。</summary>
    [Fact]
    public void FullClassName_IncludesNamespace()
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText("namespace A.B { class C { } }");
        ClassDeclarationSyntax node = tree.GetRoot()
            .DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .Single();

        Assert.Equal("A.B.C", FullClassName(node));
    }

    [GeneratedRegex("x:Class\\s*=\\s*\"(?<class>[^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex ClassPattern();

    /// <summary>匹配「属性名 = 标识符值」：事件属性与普通属性共用同一采集入口（支持 <c>Class.Method</c>）。</summary>
    [GeneratedRegex(
        "(?<![A-Za-z0-9_.])(?<attribute>[A-Za-z_][A-Za-z0-9_.:]*)\\s*=\\s*\"(?<value>[A-Za-z_][A-Za-z0-9_.]*)\"",
        RegexOptions.CultureInvariant)]
    private static partial Regex AttributePattern();

    /// <summary>
    /// 框架事件名固定集合（WinUI/UIElement 常用事件）。
    /// 识别规则为「已知事件名精确匹配」或「事件名后缀（含 <c>*Changed</c>）且不带状态前缀」，
    /// 不再声称只认明确事件名。
    /// </summary>
    private static readonly HashSet<string> KnownEventNames = new(StringComparer.Ordinal)
    {
        "Click", "Checked", "Unchecked", "Toggled", "SelectionChanged", "TextChanged", "Loaded", "Unloaded",
        "Closing", "Closed", "DragOver", "Drop", "DragEnter", "DragLeave", "DragStarting", "DropCompleted",
        "RightTapped", "DoubleTapped", "Tapped", "PointerPressed", "PointerReleased", "PointerEntered",
        "PointerExited", "PointerMoved", "PointerWheelChanged", "SizeChanged", "ValueChanged", "DateChanged",
        "TimeChanged", "Expanding", "Collapsed", "Invoked", "ItemClick", "Opened", "PrimaryButtonClick",
        "SecondaryButtonClick", "KeyDown", "KeyUp", "ContextRequested", "CharacterReceived",
        "IsEnabledChanged", "IsCheckedChanged", "GotFocus", "LostFocus", "PointerCaptureLost",
        "PointerCanceled", "DragEnded", "ItemInvoked", "Holding",
    };

    /// <summary>
    /// 属性是否按事件处理器处理：已知事件名精确匹配，<c>*Changed</c> 后缀直接视为事件
    /// （如 <c>IsHitTestVisibleChanged</c>），其余非状态属性（<c>Is*/Allow*/Can*/Has*</c>）
    /// 以已知事件名结尾（如 <c>CustomClick</c>）。
    /// </summary>
    /// <param name="attribute">XAML 属性名（可带命名空间前缀）。</param>
    /// <returns>事件属性时返回 <see langword="true"/>。</returns>
    private static bool IsEventAttribute(string attribute)
    {
        string name = attribute[(attribute.LastIndexOf('.') + 1)..];
        if (KnownEventNames.Contains(name))
        {
            return true;
        }

        // Changed 是明确的事件后缀，IsHitTestVisibleChanged 不应因 Is* 前缀被静默跳过。
        if (name.EndsWith("Changed", StringComparison.Ordinal))
        {
            return true;
        }

        // IsChecked/AllowDrop 这类状态属性不能按「Checked/Drop 结尾」误判为事件。
        bool stateLike = name.StartsWith("Is", StringComparison.Ordinal)
            || name.StartsWith("Allow", StringComparison.Ordinal)
            || name.StartsWith("Can", StringComparison.Ordinal)
            || name.StartsWith("Has", StringComparison.Ordinal);
        return !stateLike
            && KnownEventNames.Any(eventName => name.EndsWith(eventName, StringComparison.Ordinal));
    }

    /// <summary>方法名是否以「_事件名」结尾（只服务反向检查）。</summary>
    /// <param name="name">方法名。</param>
    /// <returns>形似事件处理器时返回 <see langword="true"/>。</returns>
    private static bool LooksLikeEventHandlerName(string name) =>
        KnownEventNames.Any(eventName => name.EndsWith("_" + eventName, StringComparison.Ordinal));

    /// <summary>
    /// 事件处理器的语法形状：两个参数，第一个是 <c>object</c>，第二个类型名以 <c>EventArgs</c> 结尾
    /// （支持限定类型）。
    /// </summary>
    /// <param name="method">方法声明。</param>
    /// <returns>符合事件处理器形状时返回 <see langword="true"/>。</returns>
    private static bool HasEventHandlerShape(MethodDeclarationSyntax method)
    {
        SeparatedSyntaxList<ParameterSyntax> parameters = method.ParameterList.Parameters;
        return parameters.Count == 2
            && parameters[0].Type is PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.ObjectKeyword }
            && TypeName(parameters[1].Type).EndsWith("EventArgs", StringComparison.Ordinal);
    }

    /// <summary>取类型语法的最后一段名称（支持 Identifier/Qualified/AliasQualified）。</summary>
    /// <param name="type">类型语法。</param>
    /// <returns>类型短名；无法识别时为空串。</returns>
    private static string TypeName(TypeSyntax? type) => type switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
        _ => string.Empty,
    };
}
