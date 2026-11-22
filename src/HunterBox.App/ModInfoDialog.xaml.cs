using HunterBox.Domain.Mods;
using Microsoft.UI.Xaml.Controls;

namespace HunterBox.App;

/// <summary>「MOD信息设置」对话框：编辑名称、分类、版本、作者、NexusID、来源与备注。</summary>
public sealed partial class ModInfoDialog : ContentDialog
{
    /// <summary>用现有元数据填充控件。</summary>
    /// <param name="metadata">当前元数据。</param>
    public ModInfoDialog(ModMetadata metadata)
    {
        InitializeComponent();

        NameBox.Text = metadata.Name ?? string.Empty;
        CategoryBox.Text = metadata.Category ?? string.Empty;
        VersionBox.Text = metadata.Version ?? string.Empty;
        AuthorBox.Text = metadata.Author ?? string.Empty;
        NexusBox.Text = metadata.NexusId?.ToString() ?? string.Empty;
        HomeUrlBox.Text = metadata.HomeUrl ?? string.Empty;
        DescriptionBox.Text = metadata.Description ?? string.Empty;
    }

    /// <summary>按控件内容生成新的元数据对象。</summary>
    /// <returns>编辑后的元数据。</returns>
    public ModMetadata BuildResult() => new()
    {
        Name = NullIfEmpty(NameBox.Text),
        Category = NullIfEmpty(CategoryBox.Text),
        Version = NullIfEmpty(VersionBox.Text),
        Author = NullIfEmpty(AuthorBox.Text),
        NexusId = int.TryParse(NexusBox.Text, out int nexus) && nexus > 0 ? nexus : null,
        HomeUrl = NullIfEmpty(HomeUrlBox.Text),
        Description = NullIfEmpty(DescriptionBox.Text),
    };

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
