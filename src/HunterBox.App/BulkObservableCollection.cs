using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace HunterBox.App;

/// <summary>
/// 支持整体替换的 <see cref="ObservableCollection{T}"/>：一次替换只发一次 Reset 通知，
/// 避免列表过滤/刷新时逐项 <c>Add</c> 触发 N 次布局。
/// </summary>
/// <typeparam name="T">元素类型。</typeparam>
internal sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>用给定内容整体替换集合，只触发一次 <see cref="NotifyCollectionChangedAction.Reset"/>。</summary>
    /// <param name="items">新内容（按显示顺序）。</param>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> 为 <see langword="null"/>。</exception>
    public void ReplaceAll(IReadOnlyList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        Items.Clear();
        foreach (T item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
