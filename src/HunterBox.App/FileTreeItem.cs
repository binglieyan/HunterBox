using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace HunterBox.App;

/// <summary>安装对话框里的文件树节点（支持多根标记）。</summary>
public sealed partial class FileTreeItem : INotifyPropertyChanged
{
    private bool _isChecked = true;
    private bool _isRoot;

    /// <summary>显示名（文件名或目录名）。</summary>
    public required string Name { get; init; }

    /// <summary>相对内容根的路径（文件与目录都有）。</summary>
    public required string RelativePath { get; init; }

    /// <summary>是否为文件节点。</summary>
    public bool IsFile { get; init; }

    /// <summary>子节点。</summary>
    public ObservableCollection<FileTreeItem> Children { get; } = [];

    /// <summary>父节点。</summary>
    public FileTreeItem? Parent { get; set; }

    /// <summary>是否被标记为安装根。</summary>
    public bool IsRoot
    {
        get => _isRoot;
        set
        {
            if (_isRoot == value)
            {
                return;
            }

            _isRoot = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayName));
        }
    }

    /// <summary>树里显示的文本：根节点带 <c>[根]</c> 前缀（对齐 TreeFileNode）。</summary>
    public string DisplayName => IsRoot ? "[根] " + Name : Name;

    /// <summary>是否勾选；勾选会级联到子节点与父节点的“全选”状态。</summary>
    /// <remarks>
    /// 级联使用不回调父节点的内部递归，最后只刷新一次父链；旧实现每改一个子节点都
    /// 向上 RefreshFromChildren，全选/全不选会退化成 O(n²)。
    /// </remarks>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
            {
                return;
            }

            SetCheckedCore(value, notify: true);
            Parent?.RefreshFromChildren();
        }
    }

    private void SetCheckedCore(bool value, bool notify)
    {
        if (_isChecked != value)
        {
            _isChecked = value;
            if (notify)
            {
                OnPropertyChanged(nameof(IsChecked));
            }
        }

        foreach (FileTreeItem child in Children)
        {
            child.SetCheckedCore(value, notify);
        }
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>收集所有被勾选的文件相对路径。</summary>
    /// <returns>相对路径集合。</returns>
    public IEnumerable<string> SelectedFiles()
    {
        if (IsFile && IsChecked)
        {
            yield return RelativePath;
        }

        foreach (FileTreeItem child in Children)
        {
            foreach (string path in child.SelectedFiles())
            {
                yield return path;
            }
        }
    }

    /// <summary>按子节点状态更新自己的勾选状态（不全选则视为未勾选）。</summary>
    public void RefreshFromChildren()
    {
        if (Children.Count == 0)
        {
            return;
        }

        bool allChecked = Children.All(c => c.IsChecked);
        if (_isChecked != allChecked)
        {
            _isChecked = allChecked;
            OnPropertyChanged(nameof(IsChecked));
        }

        Parent?.RefreshFromChildren();
    }

    /// <summary>按子节点状态<paramref name="value"/>递归设置根标记。</summary>
    /// <param name="value">是否标记为根。</param>
    public void SetRootRecursive(bool value)
    {
        IsRoot = value;
        foreach (FileTreeItem child in Children)
        {
            child.SetRootRecursive(value);
        }
    }

    /// <summary>当前子树里被标记为根的节点。</summary>
    /// <returns>根节点集合（按树顺序）。</returns>
    public IEnumerable<FileTreeItem> CollectRoots()
    {
        if (IsRoot)
        {
            yield return this;
        }

        foreach (FileTreeItem child in Children)
        {
            foreach (FileTreeItem root in child.CollectRoots())
            {
                yield return root;
            }
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
