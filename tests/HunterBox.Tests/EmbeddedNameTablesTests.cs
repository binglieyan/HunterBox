using HunterBox.Infrastructure.Mods;

namespace HunterBox.Tests;

public class EmbeddedNameTablesTests
{
    [Fact]
    public void LoadWeapons_ReturnsRowsWithType()
    {
        IReadOnlyList<NameTableRow> rows = EmbeddedNameTables.LoadWeapons();

        Assert.True(rows.Count > 300, $"武器表行数过少：{rows.Count}");
        Assert.Contains(rows, r => r.Type.Length > 0 && r.Name.Length > 0);
    }

    [Fact]
    public void LoadArmors_ReturnsRows()
    {
        IReadOnlyList<NameTableRow> rows = EmbeddedNameTables.LoadArmors();

        Assert.True(rows.Count > 50, $"护甲表行数过少：{rows.Count}");
        Assert.Contains(rows, r => r.Id.Contains('_'));
    }

    [Fact]
    public void Parse_SkipsCommentsAndReadsColumns()
    {
        const string content = "\uFEFF#weapon id\nBOW,00_0000,希望之弓/期冀之弓\nGS,00_0001,炎尾大剑\n";

        IReadOnlyList<NameTableRow> rows = EmbeddedNameTables.Parse(content, withType: true);

        Assert.Equal(2, rows.Count);
        Assert.Equal("BOW", rows[0].Type);
        Assert.Equal("00_0000", rows[0].Id);
        Assert.Equal("希望之弓/期冀之弓", rows[0].Name);
    }

    [Fact]
    public void Parse_WithoutType_PutsIdFirst()
    {
        IReadOnlyList<NameTableRow> rows = EmbeddedNameTables.Parse("#armor id\n000_000,身体(男款)\n", withType: false);

        NameTableRow row = Assert.Single(rows);
        Assert.Equal("000_000", row.Id);
        Assert.Equal("身体(男款)", row.Name);
    }

    [Fact]
    public void FindName_MatchesCaseInsensitively()
    {
        IReadOnlyList<NameTableRow> rows = EmbeddedNameTables.Parse("BOW,00_0000,希望之弓\n", withType: true);

        Assert.Equal("希望之弓", EmbeddedNameTables.FindName(rows, "00_0000"));
        Assert.Null(EmbeddedNameTables.FindName(rows, "99_9999"));
    }
}
